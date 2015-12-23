﻿using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using m.Http.Backend;
using m.Http.Backend.Tcp;
using m.Logging;
using m.Utils;

namespace m.Http
{
    public class TcpListenerBackend
    {
        readonly LoggingProvider.ILogger logger = LoggingProvider.GetLogger(typeof(TcpListenerBackend));

        readonly string name;
        readonly int maxKeepAlives;
        readonly int backlog;
        readonly int sessionReadBufferSize;
        readonly TimeSpan sessionReadTimeout;
        readonly TimeSpan sessionWriteTimeout;
        readonly TcpListener listener;
        readonly LifeCycleToken lifeCycleToken;

        readonly ConcurrentDictionary<long, Session> sessionTable;
        readonly ConcurrentDictionary<long, long> sessionReads;

        readonly ConcurrentDictionary<long, WebSocketSession> webSocketSessionTable; //TODO: track dead reads ?

        readonly WaitableTimer timer;

        long acceptedSessions = 0;
        long acceptedWebSocketSessions = 0;

        Router router;

        public TcpListenerBackend(IPAddress address,
                                  int port,
                                  int maxKeepAlives=100,
                                  int backlog=128,
                                  int sessionReadBufferSize=1024,
                                  int sessionReadTimeoutMs=5000,
                                  int sessionWriteTimeoutMs=5000)
        {
            listener = new TcpListener(address, port);
            this.maxKeepAlives = maxKeepAlives;
            this.backlog = backlog;
            this.sessionReadBufferSize = sessionReadBufferSize;
            sessionReadTimeout = TimeSpan.FromMilliseconds(sessionReadTimeoutMs);
            sessionWriteTimeout = TimeSpan.FromMilliseconds(sessionWriteTimeoutMs);
            lifeCycleToken = new LifeCycleToken();
            sessionTable = new ConcurrentDictionary<long, Session>();
            sessionReads = new ConcurrentDictionary<long, long>();
            webSocketSessionTable = new ConcurrentDictionary<long, WebSocketSession>();

            name = string.Format("TcpListenerBackend({0}:{1})", address, port);

            timer = new WaitableTimer("TcpListenerBackendTimer",
                                      TimeSpan.FromSeconds(1),
                                      new [] {
                                          new WaitableTimer.Job("CheckSessionReadTimeouts", CheckSessionReadTimeouts)
                                      });
        }

        public void Start(RouteTable routeTable)
        {
            Start(new Router(routeTable));
        }

        public void Start(Router router)
        {
            if (lifeCycleToken.Start())
            {
                timer.Start();

                this.router = router;
                this.router.Start();

                var acceptorLoopThread = new Thread(AcceptorLoop)
                {
                    Priority = ThreadPriority.AboveNormal,
                    IsBackground = false,
                    Name = name
                };

                acceptorLoopThread.Start();
            }
        }

        public void Shutdown()
        {
            if (lifeCycleToken.Shutdown())
            {
                timer.Shutdown();
                listener.Stop();
            }
        }

        void AcceptorLoop()
        {
            listener.Start(backlog);
            logger.Info("Listening on {0}", listener.LocalEndpoint);


            while (true)
            {
                try
                {
                    var client = listener.AcceptTcpClient();
                    var sessionId = ++acceptedSessions;

                    Task.Run(() => HandleSession(new Session(sessionId, client, maxKeepAlives, sessionReadBufferSize, sessionReadTimeout, sessionWriteTimeout)));
                }
                catch (SocketException e)
                {
                    if (lifeCycleToken.IsShutdown) // triggered by listener.Stop()
                    {
                        logger.Info("Listener shutting down");
                        break;
                    }
                    else
                    {
                        logger.Error("Exception while accepting TcpClient - {0}", e.ToString());
                    }
                }
            }

            logger.Info("Listener closed (accepted: {0})", acceptedSessions);

            router.Shutdown();
        }

        async Task HandleSession(Session session)
        {
            TrackSession(session);
            try
            {
                var continueSession = true;
                while (continueSession && !session.IsDisconnected())
                {
                    try
                    {
                        TrackSessionRead(session.Id);
                        if (await session.ReadToBufferAsync().ConfigureAwait(false) == 0) // 0 => client clean disconnect
                        {
                            break;
                        }
                    }
                    finally
                    {
                        UntrackSessionRead(session.Id);
                    }

                    HttpRequest request;
                    while (continueSession && session.TryParseNextRequestFromBuffer(out request))
                    {
                        HttpResponse response = await router.HandleRequest(request, DateTime.UtcNow).ConfigureAwait(false);

                        var webSocketUpgradeResponse = response as WebSocketUpgradeResponse;
                        if (webSocketUpgradeResponse == null)
                        {
                            session.WriteResponse(response, request.IsKeepAlive);
                            continueSession = request.IsKeepAlive && session.KeepAlivesRemaining > 0;
                        }
                        else
                        {
                            if (HandleWebsocketUpgrade(session, webSocketUpgradeResponse))
                            {
                                return;
                            }
                            else
                            {
                                continueSession = false;
                            }
                        }
                    }
                }
            }
            catch (RequestException e)
            {
                logger.Warn("Error parsing or bad request - {0}", e.Message);
            }
            catch (SessionStreamException)
            {
                // forced disconnect, socket errors
            }
            catch (Exception e)
            {
                logger.Fatal("Internal server error handling session - {0}", e.ToString());
            }
            finally
            {
                UntrackSession(session.Id);
            }

            session.Dispose();
        }

        bool HandleWebsocketUpgrade(Session session, WebSocketUpgradeResponse response)
        {
            session.WriteWebSocketUpgradeResponse(response);

            var acceptUpgradeResponse = response as WebSocketUpgradeResponse.AcceptUpgradeResponse;
            if (acceptUpgradeResponse == null)
            {
                return false;
            }
            else
            {
                long id = Interlocked.Increment(ref acceptedWebSocketSessions);
                var webSocketSession = new WebSocketSession(id, session.TcpClient, () => UntrackWebSocketSession(id));
                TrackWebSocketSession(webSocketSession);

                try
                {
                    acceptUpgradeResponse.OnAccepted(webSocketSession); //TODO: Task.Run this?
                    return true;
                }
                catch (Exception e)
                {
                    UntrackWebSocketSession(id);
                    logger.Error("Error calling WebSocketUpgradeResponse.OnAccepted callback - {0}", e.ToString());
                    return false;
                }
            }
        }

        void TrackSession(Session session)
        {
            sessionTable[session.Id] = session;
        }

        void UntrackSession(long id)
        {
            Session _;
            sessionTable.TryRemove(id, out _);
        }

        void TrackSessionRead(long id)
        {
            sessionReads[id] = Time.CurrentTimeMillis;
        }

        void UntrackSessionRead(long id)
        {
            long _;
            sessionReads.TryRemove(id, out _);
        }

        void TrackWebSocketSession(WebSocketSession session)
        {
            webSocketSessionTable[session.Id] = session;
        }

        void UntrackWebSocketSession(long id)
        {
            WebSocketSession _;
            webSocketSessionTable.TryRemove(id, out _);
        }

        void CheckSessionReadTimeouts()
        {
            var now = Time.CurrentTimeMillis;

            foreach (var kvp in sessionReads)
            {
                if (now - kvp.Value > sessionReadTimeout.TotalMilliseconds)
                {
                    sessionTable[kvp.Key].Dispose();
                }
            }
        }

        public object GetMetricsReport() //TODO: typed report
        {
            if (!lifeCycleToken.IsStarted)
            {
                throw new InvalidOperationException("Not started");
            }

            return new
            {
                Sessions = sessionTable.Count,
                WebSocketSessions = webSocketSessionTable.Count,
                Router = router.Metrics.GetReports(),
            };
        }
    }
}
