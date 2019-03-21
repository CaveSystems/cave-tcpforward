using Cave;
using Cave.Console;
using Cave.Logging;
using Cave.Net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace tcpforward
{
    class Program
    {
        static readonly Dictionary<IPEndPoint, TcpListener> LocalEndPoints = new Dictionary<IPEndPoint, TcpListener>();
        static readonly List<IPEndPoint> TargetEndPoints = new List<IPEndPoint>();
        static bool Exit;
        static bool ShowConnects;
        static bool Quiet;
        static bool ShowStatistics;
        static int ConnectionOffset = 0;
        static LogConsole LogConsole;
        static Logger Log = new Logger("tcpforward");
        static bool m_HeaderDisplayed = false;

        static void Header()
        {
            if (m_HeaderDisplayed) return;
            m_HeaderDisplayed = true;
            if (Quiet) return;
            AssemblyVersionInfo v = AssemblyVersionInfo.FromAssembly(typeof(Program).Assembly);
            Log.LogInfo("<yellow>" + v.Title + "<cyan> v" + v.AssemblyVersion + " <default>" + v.Configuration + " " + v.FileVersion + " " + v.Copyright + "\n\n");
        }

        static void Usage()
        {
            Exit = true;
            Header();
            Log.LogInfo("\n" +
                "Usage: tcpforward <yellow><-s=source> <-t=target><cyan> [-st] [-sc]\n" +
                "\n" +
                "<yellow>Needed options:\n" +
                "\t-s=source\n" +
                "\t\tSet the local endpoint to listen at. This can be an [ipaddress]:port or [dnsname]:port.\n" +
                "\n" +
                "\t-t=target\n" +
                "\t\tSet the target endpoint any connection will be forwarded to. This can be an [ipaddress]:port or [dnsname]:port.\n" +
                "\n" +
                "<cyan>Optional options:\n" +
                "\t\t-v\n" +
                "\t\tDisplay debug and verbose messages.\n" +
                "\n" +
                "\t\t-sc\n" +
                "\t\tDisplays each connect and disconnect message.\n" +
                "\n" +
                "\t\t-st\n" +
                "\t\tDisplays the status after each connection has be closed.\n" +
                "\n" +
                "\t-q\n" +
                "\t\tQuiet. The program runs without displaying messages.\n" +
                "\n" +
                "Examples: tcpforward -s=localhost:123,0.0.0.0:234,345 -t=1234:5678:9abc::1:123\n\n");
        }

        static void ParseArgs()
        {
            try
            {
                Arguments arguments = Arguments.FromEnvironment();
                if (arguments.IsHelpOptionFound()) { Usage(); return; }
                var invalid = arguments.GetInvalidOptions("v", "verbose", "q", "quiet", "s", "source", "t", "target", "sc", "st", "show-connects", "show-statistics");
                if (invalid.Count > 0)
                {
                    throw new Exception(string.Format("Invalid options {0} found!", invalid.Join(",")));
                }
                Quiet = arguments.IsOptionPresent("q");
                if (!Quiet) Header();
                #region -v verbose option
                if (arguments.IsOptionPresent("v") || arguments.IsOptionPresent("verbose"))
                {
                    if (Quiet) throw new Exception("You cannot use quiet and verbose option!");
                    LogConsole.Level = LogLevel.Verbose;
                    Log.LogVerbose("<red>Verbose<default>Mode");
                }
                #endregion
                #region -s=source option
                if (arguments.IsOptionPresent("s") || arguments.IsOptionPresent("source"))
                {
                    string source = arguments.IsOptionPresent("s") ? "s" : "source";
                    foreach (string part in arguments.Options[source].Value.Split(','))
                    {
                        foreach (IPEndPoint endPoint in NetTools.GetIPEndPoints(part, 80))
                        {
                            if (LocalEndPoints.ContainsKey(endPoint)) continue;
                            Log.LogVerbose(string.Format("Source <cyan>{0}<default> start listening...", endPoint));
                            TcpListener listener = new TcpListener(endPoint);
                            listener.Start();
                            LocalEndPoints.Add(endPoint, listener);
                        }
                    }
                }
                #endregion
                #region -t=target option
                if (arguments.IsOptionPresent("t") || arguments.IsOptionPresent("target"))
                {
                    string target = arguments.IsOptionPresent("t") ? "t" : "target";
                    foreach (string part in arguments.Options[target].Value.Split(','))
                    {
                        foreach (IPEndPoint endPoint in NetTools.GetIPEndPoints(part, 80))
                        {
                            if (TargetEndPoints.Contains(endPoint)) continue;
                            Log.LogVerbose(string.Format("Target <cyan>{0}<default> configured", endPoint));
                            TargetEndPoints.Add(endPoint);
                        }
                    }
                }
                else
                {
                    foreach (var ipe in LocalEndPoints)
                    {
                        var endPoint = new IPEndPoint(IPAddress.Loopback, ipe.Key.Port);
                        if (TargetEndPoints.Contains(endPoint)) continue;
                        Log.LogVerbose(string.Format("Target <cyan>{0}<default> configured", endPoint));
                        TargetEndPoints.Add(endPoint);
                    }
                }
                #endregion
                ShowConnects = arguments.IsOptionPresent("sc") || arguments.IsOptionPresent("show-connects");
                ShowStatistics = arguments.IsOptionPresent("st") || arguments.IsOptionPresent("show-statistics");
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "<red>Error <default>during startup.");
                LocalEndPoints.Clear();
                return;
            }

            if (LocalEndPoints.Count == 0) { Log.LogError("<red>Error <default>missing source option!"); Usage(); return; }
            if (TargetEndPoints.Count == 0) { Log.LogError("<red>Error <default>missing target option!"); Usage(); return; }
        }

        static TcpClient GetTargetConnection()
        {
            int i = unchecked(++ConnectionOffset);
            for (int n = 0; n < TargetEndPoints.Count; n++)
            {
                IPEndPoint l_Target = TargetEndPoints[i++ % TargetEndPoints.Count];
                try
                {
                    TcpClient l_Client = new TcpClient();
                    l_Client.Connect(l_Target);
                    return l_Client;
                }
                catch (Exception ex)
                {
                    Log.LogVerbose(string.Format("Could not connect to target {0}", l_Target), ex);
                }
            }
            throw new Exception("Could not connect to any target!");
        }

        static void Forward(TcpClient source)
        {
            source.Client.Blocking = true;
            EndPoint sourceEndPoint = source.Client.RemoteEndPoint;
            EndPoint targetEndPoint = null;
            NetworkStream sourceStream = source.GetStream();
            long source2Target = 0;
            long target2Source = 0;

            try
            {
                TcpClient target = GetTargetConnection();
                targetEndPoint = target.Client.RemoteEndPoint;
                NetworkStream targetStream = target.GetStream();
                Log.LogVerbose(string.Format("Establishing tunnel <cyan>{0} <default><-> <yellow>{1}", sourceEndPoint, targetEndPoint));

                #region Source2Target Task
                Task source2TargetTask = Task.Factory.StartNew(() =>
                {
                    try
                    {
                        while (source.Connected && target.Connected && 0 < sourceStream.CopyBlocksTo(targetStream, callback: (sender, e) => { source2Target += e.Part; })) ;
                    }
                    catch { }
                });
                #endregion
                #region Target2Source Task
                Task target2SourceTask = Task.Factory.StartNew(() =>
                {
                    try { while (source.Connected && target.Connected && 0 < targetStream.CopyBlocksTo(sourceStream, callback: (sender, e) => { target2Source += e.Part; })) ; }
                    catch { }
                });
                #endregion

                Task.WaitAny(new[] { target2SourceTask, source2TargetTask });
                source.Close();
                target.Close();
            }
            catch (Exception ex)
            {
                Log.LogVerbose(StringExtensions.Format("Unclean tunnel exit at <cyan>{0} <default><-> <yellow>{1}", sourceEndPoint, targetEndPoint), ex);
            }

            {
                LogLevel level = ShowStatistics ? LogLevel.Information : LogLevel.Verbose;
                Log.Write(level, StringExtensions.Format("Tunnel <cyan>{0} <default><-> {1} <default>Source2Target: <yellow>{2} <default>Target2Source: <yellow>{3}",
                    sourceEndPoint, targetEndPoint, source2Target.FormatBinarySize(), target2Source.FormatBinarySize()));
            }
            {
                LogLevel level = ShowConnects ? LogLevel.Information : LogLevel.Verbose;
                Log.Write(level, string.Format("Disconnect from <cyan>{0}", sourceEndPoint));
            }
        }

        static void Main()
        {
            LogConsole = LogConsole.Create(LogConsoleFlags.None);
            LogConsole.ExceptionMode = LogExceptionMode.Full;

            ParseArgs();
            Header();

            if (LocalEndPoints.Count > 0)
            {
                if (!Quiet)
                {
                    Log.LogInfo(string.Format("Listening at <cyan>{0}", LocalEndPoints.Keys.Join("<default>, <cyan>")));
                }

                bool useConsole = true;
                try { while (SystemConsole.KeyAvailable) SystemConsole.ReadKey(); }
                catch { useConsole = false; }
                DateTime lastEscapePress = DateTime.MinValue;

                List<Task> listenTasks = new List<Task>();
                foreach (TcpListener listener in LocalEndPoints.Values)
                {
                    Task task = Task.Factory.StartNew(() =>
                    {
                        while (!Exit)
                        {
                            TcpClient l_Client = listener.AcceptTcpClient();
                            LogLevel l_Level = ShowConnects ? LogLevel.Information : LogLevel.Verbose;
                            Log.Write(l_Level, string.Format("Connect from <cyan>{0}", l_Client.Client.RemoteEndPoint));
                            Task.Factory.StartNew(() => Forward(l_Client));
                        }
                    }, TaskCreationOptions.LongRunning);
                    listenTasks.Add(task);
                }

                if (useConsole)
                {
                    while (!Exit)
                    {
                        if (SystemConsole.KeyAvailable)
                        {
                            switch (SystemConsole.ReadKey().Key)
                            {
                                case ConsoleKey.Escape:
                                    if (lastEscapePress.AddSeconds(1) <= DateTime.Now)
                                    {
                                        Exit = true;
                                        break;
                                    }
                                    lastEscapePress = DateTime.Now;
                                    Log.LogInfo("Press escape again within 1s to exit.");
                                    break;
                            }
                        }
                        Thread.Sleep(100);
                    }
                }
                else
                {
                    while (!Exit) Thread.Sleep(1000);
                }
            }
            Logger.Flush();
            LogConsole.Close();
        }
    }
}
