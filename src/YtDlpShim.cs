// yt-dlp transparent shim for SmartTV live streams.
//
// Installed as yt-dlp.exe next to the renamed real binary (yt-dlp-real.exe).
// Every call is forwarded to the real yt-dlp unchanged. When a -J/--dump-json
// result describes a live stream that only offers HLS (m3u8) formats — which
// Unreal's WmfMedia player cannot open, because Windows Media Foundation has
// no m3u8 byte-stream handler — the shim:
//
//   1. starts a background relay (yt-dlp continuously downloads the live
//      source and ffmpeg normalizes it to WMF-compatible fragmented MP4 on
//      http://127.0.0.1:<port>/live.mp4),
//   2. rewrites that format's url/protocol in the JSON before handing it to
//      the game.
//
// Normal videos pass through byte-for-byte untouched. If anything goes wrong,
// the original yt-dlp output is emitted unchanged (fail open).
//
// Compiled at install time by Install-SmartTV.ps1 using the .NET Framework
// compiler that ships with Windows:
//   csc.exe /optimize+ /out:yt-dlp.exe /r:System.Web.Extensions.dll YtDlpShim.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace YtDlpShim
{
    internal static class Program
    {
        private static string ExeDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        private static readonly object LogSync = new object();

        // The relay is intentionally detached from the short-lived yt-dlp JSON
        // request so it can keep serving the video. Track the actual game process
        // separately, otherwise yt-dlp/ffmpeg would remain alive after VotV exits.
        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static void Log(string message)
        {
            try
            {
                string relayDir = Path.Combine(ExeDir, "relay");
                Directory.CreateDirectory(relayDir);
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine;
                lock (LogSync)
                {
                    File.AppendAllText(Path.Combine(relayDir, "shim.log"), line, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }

        private static void LogTo(string path, string message)
        {
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine;
                lock (LogSync)
                {
                    File.AppendAllText(path, line, Encoding.UTF8);
                }
            }
            catch
            {
            }
        }

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length >= 9 && args[0] == "--relay-babysit")
                {
                    return Babysit(args);
                }
                return Passthrough();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[yt-dlp shim] fatal: " + ex.Message);
                Log("fatal: " + ex);
                return 1;
            }
        }

        // ------------------------------------------------------------------
        // Forward the call to yt-dlp-real.exe, rewriting live-HLS JSON output.
        // ------------------------------------------------------------------
        private static int Passthrough()
        {
            string real = Path.Combine(ExeDir, "yt-dlp-real.exe");
            if (!File.Exists(real))
            {
                Console.Error.WriteLine("[yt-dlp shim] yt-dlp-real.exe is missing next to the shim. Re-run Install-SmartTV.ps1.");
                return 1;
            }

            var psi = new ProcessStartInfo(real, GetRawArguments())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            var stderr = new StringBuilder();
            string stdout;
            int exitCode;
            using (var p = Process.Start(psi))
            {
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        lock (stderr) { stderr.AppendLine(e.Data); }
                    }
                };
                p.BeginErrorReadLine();
                stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                exitCode = p.ExitCode;
            }

            string output = stdout;
            if (exitCode == 0 && stdout.IndexOf("m3u8", StringComparison.Ordinal) >= 0 && stdout.TrimStart().StartsWith("{"))
            {
                try
                {
                    output = RewriteIfHlsOnly(stdout);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[yt-dlp shim] rewrite skipped: " + ex.Message);
                    Log("rewrite skipped: " + ex);
                    output = stdout;
                }
            }

            var outStream = Console.OpenStandardOutput();
            byte[] bytes = Encoding.UTF8.GetBytes(output);
            outStream.Write(bytes, 0, bytes.Length);
            outStream.Flush();
            lock (stderr) { Console.Error.Write(stderr.ToString()); }
            return exitCode;
        }

        private static string GetRawArguments()
        {
            string cmd = Environment.CommandLine;
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                if (end >= 0 && end + 1 < cmd.Length)
                {
                    return cmd.Substring(end + 1).TrimStart();
                }
                return string.Empty;
            }
            int space = cmd.IndexOf(' ');
            return space >= 0 ? cmd.Substring(space + 1).TrimStart() : string.Empty;
        }

        // ------------------------------------------------------------------
        // JSON rewrite: point the best combined HLS format at a local TS relay.
        // ------------------------------------------------------------------
        private static string RewriteIfHlsOnly(string json)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var root = serializer.DeserializeObject(json) as Dictionary<string, object>;
            if (root == null || !root.ContainsKey("formats"))
            {
                return json;
            }

            var formats = root["formats"] as object[];
            if (formats == null)
            {
                return json;
            }

            Dictionary<string, object> target = null;
            double targetScore = double.MinValue;
            bool hasDirectCombined = false;
            var hlsCombined = new List<Dictionary<string, object>>();
            foreach (object f in formats)
            {
                var fmt = f as Dictionary<string, object>;
                if (fmt == null)
                {
                    continue;
                }
                string vcodec = Str(fmt, "vcodec");
                string acodec = Str(fmt, "acodec");
                string protocol = Str(fmt, "protocol");
                bool combined = vcodec != "" && vcodec != "none" && acodec != "" && acodec != "none";
                if (!combined)
                {
                    continue;
                }
                if ((protocol == "https" || protocol == "http") && !hasDirectCombined)
                {
                    hasDirectCombined = true;
                }
                if (protocol.StartsWith("m3u8") && vcodec.StartsWith("avc1") && acodec.StartsWith("mp4a"))
                {
                    hlsCombined.Add(fmt);

                    // Prefer a Windows-friendly 720p30-or-lower stream. Source
                    // quality Twitch/YouTube feeds are often 1080p60 with
                    // discontinuous timestamps; Media Foundation is much more
                    // reliable with the normalized 720p30 rendition.
                    double height = Num(fmt, "height");
                    double fps = Num(fmt, "fps");
                    double tbr = Num(fmt, "tbr");
                    bool compatible = (height <= 0 || height <= 720) && (fps <= 0 || fps <= 30.5);
                    double score = (compatible ? 1000000000.0 : 0.0) +
                        Math.Min(height, 2160) * 100000.0 +
                        Math.Min(fps, 120) * 1000.0 +
                        Math.Min(tbr, 100000);
                    if (score > targetScore)
                    {
                        targetScore = score;
                        target = fmt;
                    }
                }
            }

            // Direct progressive format available (normal videos): leave alone.
            if (target == null || hasDirectCombined)
            {
                return json;
            }

            string m3u8Url = Str(target, "url");
            if (m3u8Url == "")
            {
                return json;
            }

            string videoId = Sanitize(Str(root, "id"));
            string webUrl = Str(root, "webpage_url");
            string formatId = Str(target, "format_id");
            int port = EnsureRelay(videoId, m3u8Url, webUrl, formatId);
            if (port <= 0)
            {
                return json;
            }

            string localUrl = "http://127.0.0.1:" + port + "/live.mp4";
            int rewritten = 0;
            foreach (var fmt in hlsCombined)
            {
                // The mod may select a different combined format than the one
                // used as the relay input. Point every compatible HLS choice at
                // the same local MP4 endpoint so quality ordering cannot bypass
                // the relay.
                fmt["url"] = localUrl;
                fmt["protocol"] = "http";
                fmt["ext"] = "mp4";
                fmt["container"] = "mp4";
                rewritten++;
            }

            // Some callers use the selected root format instead of the object
            // inside formats, so keep both views consistent.
            root["url"] = localUrl;
            root["protocol"] = "http";
            root["ext"] = "mp4";
            Log("relay source " + Str(target, "format_id") + " " +
                Str(target, "width") + "x" + Str(target, "height") + "@" + Str(target, "fps") +
                "; rewrote " + rewritten + " live formats to " + localUrl);
            return serializer.Serialize(root) + "\n";
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null)
            {
                return Convert.ToString(v);
            }
            return "";
        }

        private static double Num(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null)
            {
                return 0;
            }
            double result;
            return double.TryParse(Convert.ToString(v), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out result) ? result : 0;
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "unknown";
            }
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            }
            return sb.ToString();
        }

        private static int GetParentProcessId(int processId)
        {
            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == InvalidHandleValue)
            {
                return -1;
            }
            try
            {
                var entry = new PROCESSENTRY32();
                entry.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (!Process32First(snapshot, ref entry))
                {
                    return -1;
                }
                do
                {
                    if (entry.th32ProcessID == (uint)processId)
                    {
                        return (int)entry.th32ParentProcessID;
                    }
                }
                while (Process32Next(snapshot, ref entry));
            }
            finally
            {
                CloseHandle(snapshot);
            }
            return -1;
        }

        private static bool IsVotVProcess(Process process)
        {
            try
            {
                string name = (process.ProcessName ?? "").ToLowerInvariant();
                return name.Contains("votv") || name.Contains("win64-shipping");
            }
            catch
            {
                return false;
            }
        }

        private static int FindOwningGameProcessId()
        {
            // First walk the real parent chain. In normal SmartTV use the shim is
            // launched directly by VotV-Win64-Shipping.exe.
            int cursor = GetParentProcessId(Process.GetCurrentProcess().Id);
            for (int depth = 0; depth < 12 && cursor > 0; depth++)
            {
                try
                {
                    using (Process candidate = Process.GetProcessById(cursor))
                    {
                        if (IsVotVProcess(candidate))
                        {
                            return cursor;
                        }
                    }
                }
                catch
                {
                }
                cursor = GetParentProcessId(cursor);
            }

            // Some launchers break the parent chain. Fall back to the currently
            // running VotV process, preferring the newest instance.
            int bestPid = -1;
            DateTime bestStart = DateTime.MinValue;
            foreach (Process candidate in Process.GetProcesses())
            {
                try
                {
                    if (IsVotVProcess(candidate))
                    {
                        DateTime started = candidate.StartTime.ToUniversalTime();
                        if (started > bestStart)
                        {
                            bestStart = started;
                            bestPid = candidate.Id;
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    candidate.Dispose();
                }
            }
            return bestPid;
        }

        private static long GetProcessStartTicks(int processId)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    return process.StartTime.ToUniversalTime().Ticks;
                }
            }
            catch
            {
                return 0;
            }
        }

        private static bool IsSameProcessAlive(int processId, long expectedStartTicks)
        {
            if (processId <= 0)
            {
                return true; // owner detection unavailable: retain legacy lifetime
            }
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    if (process.HasExited)
                    {
                        return false;
                    }
                    if (expectedStartTicks > 0)
                    {
                        try
                        {
                            return process.StartTime.ToUniversalTime().Ticks == expectedStartTicks;
                        }
                        catch
                        {
                            // Lack of permission to read StartTime should not kill a
                            // valid game process. GetProcessById already proved it exists.
                        }
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void StartOwnerMonitor(
            int ownerPid, long ownerStartTicks, RelayServer server, string stateFile, string logFile)
        {
            if (ownerPid <= 0)
            {
                LogTo(logFile, "game owner process could not be detected; using legacy relay lifetime");
                return;
            }
            var thread = new Thread(delegate()
            {
                LogTo(logFile, "monitoring game owner pid=" + ownerPid + " startTicks=" + ownerStartTicks);
                // Require two consecutive misses to avoid reacting to a transient
                // process-enumeration failure.
                int misses = 0;
                while (!server.ShutdownRequested)
                {
                    if (IsSameProcessAlive(ownerPid, ownerStartTicks))
                    {
                        misses = 0;
                    }
                    else
                    {
                        misses++;
                        if (misses >= 2)
                        {
                            LogTo(logFile, "game owner exited pid=" + ownerPid + "; stopping relay pipeline");
                            server.RequestOwnerExitShutdown(ownerPid);
                            TryDelete(stateFile);
                            return;
                        }
                    }
                    Thread.Sleep(500);
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        // ------------------------------------------------------------------
        // Relay management. A short startup grace avoids duplicate relays from one
        // request, while later Add Video actions create a fresh live-edge session.
        // ------------------------------------------------------------------
        private static int EnsureRelay(string videoId, string m3u8Url, string webUrl, string formatId)
        {
            string relayDir = Path.Combine(ExeDir, "relay");
            Directory.CreateDirectory(relayDir);
            string stateFile = Path.Combine(relayDir, videoId + ".txt");

            // One Add Video action may query yt-dlp more than once in a short
            // burst, so reuse a relay only during a two-second startup grace period.
            // A later Add Video action for the same live URL must create a new
            // relay generation; otherwise playback begins at the moment the old
            // relay was first opened instead of the current live edge.
            if (File.Exists(stateFile))
            {
                try
                {
                    string[] parts = File.ReadAllText(stateFile).Trim().Split(' ');
                    int oldPort = int.Parse(parts[0]);
                    int oldPid = int.Parse(parts[1]);
                    long createdTicks = parts.Length >= 3 ? long.Parse(parts[2]) : 0;
                    Process oldProcess = Process.GetProcessById(oldPid); // throws if the babysitter died
                    double ageSeconds = createdTicks > 0
                        ? (DateTime.UtcNow - new DateTime(createdTicks, DateTimeKind.Utc)).TotalSeconds
                        : double.MaxValue;

                    if (ageSeconds <= 2 && WaitForRelayReady(oldPort, 3))
                    {
                        Log("reusing startup relay for " + videoId + " on port " + oldPort +
                            " age=" + ageSeconds.ToString("0.0") + "s");
                        return oldPort;
                    }

                    Log("requesting fresh live relay for " + videoId + " oldPort=" + oldPort +
                        " age=" + ageSeconds.ToString("0.0") + "s");
                    RequestRelayShutdown(oldPort);
                    try
                    {
                        if (!oldProcess.WaitForExit(5000)) oldProcess.Kill();
                    }
                    catch
                    {
                        try { oldProcess.Kill(); } catch { }
                    }
                }
                catch
                {
                }
                TryDelete(stateFile);
            }

            string ffmpeg = FindFfmpeg();
            if (ffmpeg == null)
            {
                Console.Error.WriteLine("[yt-dlp shim] ffmpeg not found; cannot relay the live stream. Re-run Install-SmartTV.ps1.");
                return -1;
            }

            int port = FindFreePort(8217, 8290);
            if (port < 0)
            {
                Console.Error.WriteLine("[yt-dlp shim] no free relay port between 8217 and 8290.");
                return -1;
            }

            string self = Process.GetCurrentProcess().MainModule.FileName;
            string logFile = Path.Combine(relayDir, videoId + ".log");
            TryDelete(logFile);
            int ownerPid = FindOwningGameProcessId();
            long ownerStartTicks = ownerPid > 0 ? GetProcessStartTicks(ownerPid) : 0;
            Log("creating relay for " + videoId + " ownerPid=" + ownerPid);
            string args = "--relay-babysit " + port + " " + Quote(ffmpeg) + " " + Quote(m3u8Url) + " " +
                Quote(Path.Combine(ExeDir, "yt-dlp-real.exe")) + " " + Quote(webUrl) + " " + Quote(stateFile) + " " + Quote(logFile) + " " + Quote(formatId) +
                " " + ownerPid + " " + ownerStartTicks;
            // ShellExecute so the babysitter does not inherit our stdout pipe;
            // otherwise the game's pipe read would block until the relay exits.
            var psi = new ProcessStartInfo(self, args)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            var babysitter = Process.Start(psi);
            File.WriteAllText(stateFile, port + " " + babysitter.Id + " " + DateTime.UtcNow.Ticks + " " + ownerPid);

            if (!WaitForRelayReady(port, 20))
            {
                Console.Error.WriteLine("[yt-dlp shim] relay did not become ready within 20s; see relay logs. Passing the original URL through.");
                Log("relay failed to become ready for " + videoId + " on port " + port);
                try { babysitter.Kill(); } catch { }
                TryDelete(stateFile);
                return -1;
            }
            Log("relay ready for " + videoId + " on port " + port);
            return port;
        }

        private static int Babysit(string[] args)
        {
            int port = int.Parse(args[1]);
            string ffmpeg = args[2];
            string m3u8 = args[3];
            string realExe = args[4];
            string webUrl = args[5];
            string stateFile = args[6];
            string logFile = args[7];
            string formatId = args[8];
            int ownerPid = -1;
            long ownerStartTicks = 0;
            if (args.Length >= 10) int.TryParse(args[9], out ownerPid);
            if (args.Length >= 11) long.TryParse(args[10], out ownerStartTicks);
            LogTo(logFile, "babysitter started on port " + port + ", format=" + formatId +
                ", ownerPid=" + ownerPid);

            // Media Foundation's HTTP byte stream opens MORE THAN ONE
            // connection when playing an mp4 URL (verified: a second SYN
            // arrives while the first connection is still established), so a
            // single-client "ffmpeg -listen 1" server cannot work. Instead,
            // ffmpeg pipes fragmented MP4 to us and we run a small multi-client
            // HTTP server: every client gets the cached init segment (ftyp +
            // moov) followed by live fragments starting at the next moof.
            var server = new RelayServer(port, logFile);
            try
            {
                server.Start();
                LogTo(logFile, "HTTP relay server listening");
                StartOwnerMonitor(ownerPid, ownerStartTicks, server, stateFile, logFile);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[yt-dlp shim] relay server failed to start: " + ex.Message);
                LogTo(logFile, "relay server failed: " + ex);
                TryDelete(stateFile);
                return 1;
            }

            var lifetime = Stopwatch.StartNew();
            int fastFails = 0;
            while (lifetime.Elapsed.TotalHours < 12 && fastFails < 4 && !server.ShutdownRequested)
            {
                var run = Stopwatch.StartNew();
                // Let yt-dlp continuously download the live stream and pipe it
                // into ffmpeg. Passing yt-dlp's temporary googlevideo manifest URL
                // directly to ffmpeg works only briefly on some YouTube lives: new
                // segments begin returning HTTP 403 when signatures/headers rotate.
                // yt-dlp owns that protocol logic and refreshes live fragments.
                string selectedFormat = string.IsNullOrEmpty(formatId) ? "best" : formatId;
                string ytdlpArgs =
                    "--no-playlist --no-progress --retries infinite --fragment-retries infinite" +
                    " --socket-timeout 20 --hls-use-mpegts -f " + Quote(selectedFormat) +
                    " -o - -- " + Quote(webUrl);
                string ffArgs =
                    "-hide_banner -nostdin -loglevel warning" +
                    " -fflags +genpts+discardcorrupt -thread_queue_size 1024 -i pipe:0" +
                    " -map 0:v:0 -map 0:a:0?" +
                    // Normalize every source to a WMF-friendly timeline.
                    " -vf " + Quote("scale=-2:720,fps=30") +
                    " -c:v libx264 -preset ultrafast -tune zerolatency" +
                    " -profile:v main -level:v 3.1 -pix_fmt yuv420p" +
                    " -g 30 -keyint_min 30 -sc_threshold 0" +
                    " -b:v 2500k -maxrate 3000k -bufsize 6000k" +
                    " -c:a aac -b:a 128k -ar 48000 -ac 2" +
                    " -af " + Quote("aresample=async=1:first_pts=0") +
                    " -fps_mode cfr -avoid_negative_ts make_zero -max_interleave_delta 0" +
                    " -f mp4 -brand mp42 -video_track_timescale 90000" +
                    " -frag_duration 1000000" +
                    " -movflags empty_moov+frag_keyframe+default_base_moof+omit_tfhd_offset+separate_moof" +
                    " -flush_packets 1 pipe:1";
                LogTo(logFile, "starting yt-dlp producer: " + ytdlpArgs);
                LogTo(logFile, "starting ffmpeg: " + ffArgs);
                var downloaderInfo = new ProcessStartInfo(realExe, ytdlpArgs)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                var ffmpegInfo = new ProcessStartInfo(ffmpeg, ffArgs)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                try
                {
                    using (var downloader = Process.Start(downloaderInfo))
                    using (var p = Process.Start(ffmpegInfo))
                    {
                        // PumpBoxes can be blocked waiting for ffmpeg output when the
                        // game closes. This watcher guarantees the entire child chain
                        // is terminated immediately after the owner monitor requests
                        // shutdown.
                        var pipelineStopWatcher = new Thread(delegate()
                        {
                            while (!server.ShutdownRequested)
                            {
                                try
                                {
                                    if (downloader.HasExited && p.HasExited) return;
                                }
                                catch { return; }
                                Thread.Sleep(250);
                            }
                            try { if (!downloader.HasExited) downloader.Kill(); } catch { }
                            try { if (!p.HasExited) p.Kill(); } catch { }
                        });
                        pipelineStopWatcher.IsBackground = true;
                        pipelineStopWatcher.Start();

                        downloader.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                        {
                            if (e.Data != null)
                            {
                                LogTo(logFile, "yt-dlp: " + e.Data);
                            }
                        };
                        p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                        {
                            if (e.Data != null)
                            {
                                LogTo(logFile, "ffmpeg: " + e.Data);
                            }
                        };
                        downloader.BeginErrorReadLine();
                        p.BeginErrorReadLine();

                        var inputPump = new Thread(delegate()
                        {
                            try
                            {
                                downloader.StandardOutput.BaseStream.CopyTo(p.StandardInput.BaseStream);
                            }
                            catch (Exception ex)
                            {
                                LogTo(logFile, "yt-dlp -> ffmpeg pipe ended: " + ex.Message);
                            }
                            finally
                            {
                                try { p.StandardInput.Close(); } catch { }
                            }
                        });
                        inputPump.IsBackground = true;
                        inputPump.Start();

                        PumpBoxes(p.StandardOutput.BaseStream, server);
                        if (server.RestartRequested || server.ShutdownRequested)
                        {
                            try { if (!downloader.HasExited) downloader.Kill(); } catch { }
                            try { if (!p.HasExited) p.Kill(); } catch { }
                        }
                        else
                        {
                            try
                            {
                                if (!p.HasExited) p.WaitForExit(5000);
                            }
                            catch { }
                        }
                        try
                        {
                            if (!downloader.HasExited) downloader.Kill();
                        }
                        catch { }
                        try { downloader.WaitForExit(5000); } catch { }
                        try
                        {
                            if (!p.HasExited) p.Kill();
                        }
                        catch { }
                        try { p.WaitForExit(5000); } catch { }

                        string downloaderCode = downloader.HasExited ? downloader.ExitCode.ToString() : "running";
                        string ffmpegCode = p.HasExited ? p.ExitCode.ToString() : "running";
                        LogTo(logFile, "pipeline exited yt-dlp=" + downloaderCode + " ffmpeg=" + ffmpegCode +
                            " after " + run.Elapsed.TotalSeconds.ToString("0.0") + "s");
                    }
                }
                catch (Exception ex)
                {
                    LogTo(logFile, "yt-dlp/ffmpeg relay exception: " + ex);
                    break;
                }
                server.ResetStream();

                if (server.ShutdownRequested)
                {
                    LogTo(logFile, "relay shutdown requested");
                    break;
                }
                if (server.RestartRequested)
                {
                    LogTo(logFile, "refreshing pipeline at the current live edge: " + server.RestartReason);
                    server.ClearRestartRequest();
                    fastFails = 0;
                    Thread.Sleep(250);
                    continue;
                }

                if (run.Elapsed.TotalSeconds < 15)
                {
                    fastFails++;
                }
                else
                {
                    fastFails = 0;
                }
                // Every iteration starts yt-dlp from the original webpage URL,
                // so signed manifests and fragment URLs are always refreshed.
                Thread.Sleep(2000);
            }
            server.Stop();
            LogTo(logFile, "babysitter stopped");
            TryDelete(stateFile);
            return 0;
        }

        // Reads ISO-BMFF boxes from ffmpeg's stdout. Boxes before the first
        // moof form the init segment; everything after is broadcast live.
        private static void PumpBoxes(Stream stream, RelayServer server)
        {
            var init = new MemoryStream();
            bool inInit = true;
            var header = new byte[8];
            while (true)
            {
                if (server.RestartRequested || server.ShutdownRequested)
                {
                    return;
                }
                if (!ReadExact(stream, header, 8))
                {
                    return;
                }
                long size = ((long)header[0] << 24) | ((long)header[1] << 16) | ((long)header[2] << 8) | header[3];
                string type = Encoding.ASCII.GetString(header, 4, 4);
                byte[] extHeader = null;
                if (size == 1)
                {
                    extHeader = new byte[8];
                    if (!ReadExact(stream, extHeader, 8))
                    {
                        return;
                    }
                    size = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        size = (size << 8) | extHeader[i];
                    }
                }
                if (size < 8 || size > 256L * 1024 * 1024)
                {
                    return; // corrupt stream
                }
                var box = new byte[size];
                Buffer.BlockCopy(header, 0, box, 0, 8);
                int offset = 8;
                if (extHeader != null)
                {
                    Buffer.BlockCopy(extHeader, 0, box, 8, 8);
                    offset = 16;
                }
                if (!ReadExact(stream, box, (int)size, offset))
                {
                    return;
                }

                if (server.RestartRequested || server.ShutdownRequested)
                {
                    return;
                }

                if (inInit)
                {
                    if (type == "moof")
                    {
                        inInit = false;
                        server.SetInit(init.ToArray());
                        server.Broadcast(type, box);
                    }
                    else
                    {
                        init.Write(box, 0, box.Length);
                    }
                }
                else
                {
                    server.Broadcast(type, box);
                }
            }
        }

        private static bool ReadExact(Stream stream, byte[] buffer, int count)
        {
            return ReadExact(stream, buffer, count, 0);
        }

        private static bool ReadExact(Stream stream, byte[] buffer, int count, int offset)
        {
            while (offset < count)
            {
                int n = stream.Read(buffer, offset, count - offset);
                if (n <= 0)
                {
                    return false;
                }
                offset += n;
            }
            return true;
        }

        private static string ResolveFresh(string realExe, string webUrl)
        {
            if (string.IsNullOrEmpty(webUrl) || !File.Exists(realExe))
            {
                return null;
            }
            try
            {
                var psi = new ProcessStartInfo(realExe,
                    "-f b -g --no-playlist --socket-timeout 15 --retries 2 -- " + Quote(webUrl))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                using (var p = Process.Start(psi))
                {
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { };
                    p.BeginErrorReadLine();
                    string stdout = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(90000))
                    {
                        try { p.Kill(); } catch { }
                        return null;
                    }
                    if (p.ExitCode != 0)
                    {
                        return null;
                    }
                    string[] lines = stdout.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = lines.Length - 1; i >= 0; i--)
                    {
                        string line = lines[i].Trim();
                        if (line.StartsWith("http"))
                        {
                            return line;
                        }
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static string FindFfmpeg()
        {
            string[] candidates =
            {
                Path.Combine(ExeDir, "ffmpeg\\ffmpeg.exe"),
                Path.Combine(ExeDir, "ffmpeg\\bin\\ffmpeg.exe"),
                Path.Combine(ExeDir, "ffmpeg.exe")
            };
            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathVar.Split(';'))
            {
                if (dir.Trim() == "")
                {
                    continue;
                }
                try
                {
                    string candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }
            return null;
        }

        private static bool IsListening(int port)
        {
            foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                if (ep.Port == port)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool WaitForListen(int port, int seconds)
        {
            for (int i = 0; i < seconds * 2; i++)
            {
                if (IsListening(port))
                {
                    return true;
                }
                Thread.Sleep(500);
            }
            return false;
        }

        private static void RequestRelayShutdown(int port)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/shutdown");
                request.Method = "GET";
                request.Timeout = 1500;
                request.ReadWriteTimeout = 1500;
                request.KeepAlive = false;
                request.Proxy = null;
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                }
            }
            catch
            {
            }
        }

        private static bool WaitForRelayReady(int port, int seconds)
        {
            for (int i = 0; i < seconds * 4; i++)
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/health");
                    request.Method = "GET";
                    request.Timeout = 1000;
                    request.ReadWriteTimeout = 1000;
                    request.KeepAlive = false;
                    request.Proxy = null;
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        if (response.StatusCode == HttpStatusCode.OK && reader.ReadToEnd().Trim() == "ready")
                        {
                            return true;
                        }
                    }
                }
                catch
                {
                }
                Thread.Sleep(250);
            }
            return false;
        }

        private static int FindFreePort(int from, int to)
        {
            for (int port = from; port <= to; port++)
            {
                if (!IsListening(port))
                {
                    return port;
                }
            }
            return -1;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }

    // Multi-client HTTP server backed by a growing spool file. Windows Media
    // Foundation does not consume a live MP4 as one purely sequential request:
    // after opening bytes=0- it commonly probes a second offset such as
    // bytes=327680-. Every absolute byte range therefore has to remain readable.
    internal sealed class RelayServer
    {
        private const long VirtualLength = 8589934592L; // 8 GiB virtual growing file
        private const int VirtualTailLength = 65536; // synthetic tail containing moov for WMF EOF probe
        private readonly int _port;
        private readonly string _logFile;
        private readonly string _spoolBase;
        private readonly object _sync = new object();
        private readonly List<TcpClient> _activeSockets = new List<TcpClient>();
        private TcpListener _listener;
        private FileStream _spoolWriter;
        private string _spoolPath;
        private byte[] _init;
        private long _availableLength;
        private int _generation;
        private volatile bool _running;
        private volatile bool _restartRequested;
        private volatile bool _shutdownRequested;
        private string _restartReason = "";
        private bool _requireFreshZeroRequest;
        private DateTime _lastRestartRequestUtc = DateTime.MinValue;

        public RelayServer(int port, string logFile)
        {
            _port = port;
            _logFile = logFile;
            string directory = Path.GetDirectoryName(logFile) ?? Path.GetTempPath();
            string name = Path.GetFileNameWithoutExtension(logFile);
            _spoolBase = Path.Combine(directory, name + "." + port + ".stream");
        }

        private void LogRequest(string message)
        {
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " http: " + message + Environment.NewLine;
                File.AppendAllText(_logFile, line, Encoding.UTF8);
            }
            catch
            {
            }
        }

        public bool RestartRequested
        {
            get { return _restartRequested; }
        }

        public bool ShutdownRequested
        {
            get { return _shutdownRequested; }
        }

        public string RestartReason
        {
            get
            {
                lock (_sync) { return _restartReason; }
            }
        }

        public void ClearRestartRequest()
        {
            lock (_sync)
            {
                _restartRequested = false;
                _restartReason = "";
            }
        }

        private void RequestLiveEdgeRestart(string reason)
        {
            lock (_sync)
            {
                if (_shutdownRequested || _restartRequested)
                {
                    return;
                }
                if ((DateTime.UtcNow - _lastRestartRequestUtc).TotalSeconds < 5)
                {
                    return;
                }
                _lastRestartRequestUtc = DateTime.UtcNow;
                _restartReason = reason ?? "playback fell behind live edge";
                _requireFreshZeroRequest = true;
                _restartRequested = true;
                Monitor.PulseAll(_sync);
            }
            LogRequest("live-edge refresh requested: " + reason);
        }

        public void RequestOwnerExitShutdown(int ownerPid)
        {
            List<TcpClient> sockets;
            lock (_sync)
            {
                if (_shutdownRequested)
                {
                    return;
                }
                _shutdownRequested = true;
                _restartRequested = true;
                _restartReason = "VotV game process exited";
                sockets = new List<TcpClient>(_activeSockets);
                _activeSockets.Clear();
                Monitor.PulseAll(_sync);
            }
            CloseSockets(sockets);
            LogRequest("game owner exited pid=" + ownerPid + "; relay shutdown requested");
        }

        private void RequestShutdown()
        {
            lock (_sync)
            {
                _shutdownRequested = true;
                _restartRequested = true;
                _restartReason = "relay replaced by a fresh Add Video session";
                Monitor.PulseAll(_sync);
            }
            LogRequest("shutdown requested by fresh Add Video session");
        }

        public bool IsReady
        {
            get
            {
                lock (_sync)
                {
                    return _init != null && _init.Length > 0 && _spoolWriter != null;
                }
            }
        }

        public void Start()
        {
            _running = true;
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            var thread = new Thread(AcceptLoop) { IsBackground = true };
            thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
            ResetStream();
        }

        public void SetInit(byte[] init)
        {
            List<TcpClient> stale;
            string oldPath;
            lock (_sync)
            {
                stale = new List<TcpClient>(_activeSockets);
                _activeSockets.Clear();
                oldPath = _spoolPath;
                CloseSpoolNoLock();
                _generation++;
                _spoolPath = _spoolBase + "." + _generation + ".mp4";
                _spoolWriter = new FileStream(
                    _spoolPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete,
                    1024 * 1024,
                    FileOptions.SequentialScan);
                _spoolWriter.Write(init, 0, init.Length);
                _spoolWriter.Flush();
                _availableLength = init.Length;
                _init = init;
                Monitor.PulseAll(_sync);
            }
            CloseSockets(stale);
            TryDeletePath(oldPath);
            LogRequest("MP4 init ready, bytes=" + (init == null ? 0 : init.Length) + " spool=" + _spoolPath);
        }

        // Called when ffmpeg restarts. Active readers must reconnect because the
        // new init segment and fragments belong to a new MP4 generation.
        public void ResetStream()
        {
            List<TcpClient> stale;
            string oldPath;
            lock (_sync)
            {
                _init = null;
                _generation++;
                _availableLength = 0;
                oldPath = _spoolPath;
                _spoolPath = null;
                CloseSpoolNoLock();
                stale = new List<TcpClient>(_activeSockets);
                _activeSockets.Clear();
                Monitor.PulseAll(_sync);
            }
            CloseSockets(stale);
            TryDeletePath(oldPath);
        }

        public void Broadcast(string boxType, byte[] box)
        {
            lock (_sync)
            {
                if (_spoolWriter == null)
                {
                    return;
                }
                _spoolWriter.Write(box, 0, box.Length);
                // One flush per ISO-BMFF box keeps newly appended ranges visible
                // to parallel FileStreams without buffering several fragments.
                _spoolWriter.Flush();
                _availableLength += box.Length;
                Monitor.PulseAll(_sync);
            }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient tcp;
                try
                {
                    tcp = _listener.AcceptTcpClient();
                }
                catch
                {
                    return;
                }
                var thread = new Thread(delegate() { HandleClient(tcp); }) { IsBackground = true };
                thread.Start();
            }
        }

        private void HandleClient(TcpClient tcp)
        {
            bool registered = false;
            bool caughtUpToLiveEdge = false;
            long bytesSent = 0;
            long responseStart = 0;
            var connectionAge = Stopwatch.StartNew();
            try
            {
                tcp.SendTimeout = 4000;
                tcp.ReceiveTimeout = 10000;
                tcp.SendBufferSize = 256 * 1024;
                tcp.NoDelay = true;
                lock (_sync)
                {
                    _activeSockets.Add(tcp);
                    registered = true;
                }
                NetworkStream stream = tcp.GetStream();

                var request = new byte[16384];
                int total = 0;
                while (total < request.Length)
                {
                    int n = stream.Read(request, total, request.Length - total);
                    if (n <= 0) return;
                    total += n;
                    if (Encoding.ASCII.GetString(request, 0, total).IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0)
                    {
                        break;
                    }
                }

                string requestText = Encoding.ASCII.GetString(request, 0, total);
                string firstLine = requestText.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                string[] firstParts = firstLine.Split(' ');
                string method = firstParts.Length > 0 ? firstParts[0].ToUpperInvariant() : "GET";
                string path = firstParts.Length > 1 ? firstParts[1] : "/";
                string userAgent = HeaderValue(requestText, "User-Agent");
                string range = HeaderValue(requestText, "Range");
                LogRequest(method + " " + path +
                    (range == "" ? "" : " range=" + range) +
                    (userAgent == "" ? "" : " ua=" + userAgent));

                if (method == "OPTIONS")
                {
                    WriteAscii(stream,
                        "HTTP/1.1 204 No Content\r\n" +
                        "Allow: GET, HEAD, OPTIONS\r\n" +
                        "Access-Control-Allow-Origin: *\r\n" +
                        "Connection: close\r\n\r\n");
                    return;
                }

                if (path.StartsWith("/shutdown", StringComparison.OrdinalIgnoreCase))
                {
                    RequestShutdown();
                    byte[] body = Encoding.ASCII.GetBytes("stopping");
                    WriteAscii(stream,
                        "HTTP/1.1 200 OK\r\n" +
                        "Content-Type: text/plain\r\n" +
                        "Content-Length: " + body.Length + "\r\n" +
                        "Connection: close\r\n\r\n");
                    stream.Write(body, 0, body.Length);
                    return;
                }

                if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] body = Encoding.ASCII.GetBytes(IsReady ? "ready" : "starting");
                    WriteAscii(stream,
                        "HTTP/1.1 200 OK\r\n" +
                        "Content-Type: text/plain\r\n" +
                        "Content-Length: " + body.Length + "\r\n" +
                        "Cache-Control: no-store\r\n" +
                        "Connection: close\r\n\r\n");
                    stream.Write(body, 0, body.Length);
                    return;
                }

                long rangeStart;
                long rangeEnd;
                bool hasRange;
                if (!TryParseRange(range, out hasRange, out rangeStart, out rangeEnd))
                {
                    WriteAscii(stream,
                        "HTTP/1.1 416 Range Not Satisfiable\r\n" +
                        "Content-Range: bytes */" + VirtualLength + "\r\n" +
                        "Connection: close\r\n\r\n");
                    LogRequest("rejected invalid range=" + range);
                    return;
                }
                responseStart = rangeStart;

                lock (_sync)
                {
                    if (_requireFreshZeroRequest && rangeStart == 0)
                    {
                        _requireFreshZeroRequest = false;
                        LogRequest("fresh live generation accepted a new bytes=0 request");
                    }
                    else if (_requireFreshZeroRequest && rangeStart > 0 &&
                        rangeStart < VirtualLength - VirtualTailLength)
                    {
                        WriteAscii(stream,
                            "HTTP/1.1 416 Range Not Satisfiable\r\n" +
                            "Content-Range: bytes */" + VirtualLength + "\r\n" +
                            "Cache-Control: no-store\r\n" +
                            "Connection: close\r\n\r\n");
                        LogRequest("rejected stale resume range start=" + rangeStart +
                            "; waiting for bytes=0 at the refreshed live edge");
                        return;
                    }
                }

                if (method == "HEAD")
                {
                    string headHeaders = BuildMediaHeaders(hasRange, rangeStart, rangeEnd, true);
                    WriteAscii(stream, headHeaders);
                    LogRequest("HEAD completed status=" + (hasRange ? "206" : "200") + " start=" + rangeStart);
                    return;
                }

                // WMF probes the last 16 KiB of the advertised file before it
                // begins decoding. A real live file can never grow to our 8 GiB
                // virtual length, so waiting for that offset stalls playback.
                // Serve a synthetic valid MP4 tail containing the init moov box.
                if (rangeStart >= VirtualLength - VirtualTailLength)
                {
                    byte[] init;
                    lock (_sync) { init = _init; }
                    if (init == null)
                    {
                        WriteAscii(stream,
                            "HTTP/1.1 503 Service Unavailable\r\n" +
                            "Retry-After: 1\r\n" +
                            "Connection: close\r\n\r\n");
                        LogRequest("virtual tail requested before init start=" + rangeStart);
                        return;
                    }

                    // Build the response for the exact requested slice. WMF's
                    // observed EOF probe is the final 16 KiB. Returning a fixed
                    // 64 KiB tail slice made that request begin halfway through
                    // a free box; this version starts with a real box header.
                    long requested = Math.Min(rangeEnd - rangeStart + 1, VirtualLength - rangeStart);
                    int count = (int)Math.Min(VirtualTailLength, requested);
                    byte[] tail = BuildVirtualTail(init, count);
                    WriteAscii(stream, BuildMediaHeaders(true, rangeStart, rangeStart + tail.Length - 1, true));
                    stream.Write(tail, 0, tail.Length);
                    bytesSent += tail.Length;
                    LogRequest("served synthetic MP4 tail start=" + rangeStart + " bytes=" + tail.Length +
                        " moov=" + (FindTopLevelBox(init, "moov") != null));
                    return;
                }

                string spoolPath;
                int generation;
                if (!WaitForOffset(rangeStart, 30, out spoolPath, out generation))
                {
                    WriteAscii(stream,
                        "HTTP/1.1 503 Service Unavailable\r\n" +
                        "Retry-After: 1\r\n" +
                        "Connection: close\r\n\r\n");
                    LogRequest("range start not ready after timeout start=" + rangeStart + " available=" + GetAvailableLength());
                    return;
                }

                WriteAscii(stream, BuildMediaHeaders(hasRange, rangeStart, rangeEnd, false));
                LogRequest("stream response status=" + (hasRange ? "206" : "200") +
                    " start=" + rangeStart + " end=" + rangeEnd + " available=" + GetAvailableLength());

                using (var reader = new FileStream(
                    spoolPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    1024 * 1024,
                    FileOptions.SequentialScan))
                {
                    reader.Position = rangeStart;
                    long position = rangeStart;
                    var buffer = new byte[256 * 1024];
                    while (_running && position <= rangeEnd)
                    {
                        long available;
                        int currentGeneration;
                        lock (_sync)
                        {
                            while (_running && generation == _generation && _availableLength <= position)
                            {
                                Monitor.Wait(_sync, 1000);
                            }
                            available = _availableLength;
                            currentGeneration = _generation;
                        }
                        if (!_running || currentGeneration != generation)
                        {
                            break;
                        }

                        long liveLag = available - position;
                        if (liveLag <= 512L * 1024)
                        {
                            caughtUpToLiveEdge = true;
                        }
                        else if (caughtUpToLiveEdge && connectionAge.Elapsed.TotalSeconds >= 6 &&
                            liveLag > 4L * 1024 * 1024)
                        {
                            RequestLiveEdgeRestart("paused/stalled client lagged " +
                                (liveLag / 1024) + " KiB behind the live edge");
                            break;
                        }

                        long remaining = rangeEnd - position + 1;
                        long ready = liveLag;
                        int wanted = (int)Math.Min(buffer.Length, Math.Min(remaining, ready));
                        if (wanted <= 0)
                        {
                            continue;
                        }
                        reader.Position = position;
                        int read = reader.Read(buffer, 0, wanted);
                        if (read <= 0)
                        {
                            Thread.Sleep(10);
                            continue;
                        }
                        stream.Write(buffer, 0, read);
                        position += read;
                        bytesSent += read;
                    }
                }
            }
            catch (Exception ex)
            {
                LogRequest("client exception start=" + responseStart + " sent=" + bytesSent + ": " + ex.Message);
                if (!_shutdownRequested && caughtUpToLiveEdge &&
                    connectionAge.Elapsed.TotalSeconds >= 6 && bytesSent >= 1024L * 1024)
                {
                    RequestLiveEdgeRestart("playback connection stopped consuming data after " +
                        connectionAge.Elapsed.TotalSeconds.ToString("0.0") + "s");
                }
            }
            finally
            {
                if (registered)
                {
                    lock (_sync)
                    {
                        _activeSockets.Remove(tcp);
                    }
                }
                try { tcp.Close(); } catch { }
                LogRequest("stream client disconnected start=" + responseStart + " sent=" + bytesSent);
            }
        }

        private string BuildMediaHeaders(bool partial, long start, long end, bool close)
        {
            long length = end - start + 1;
            string headers =
                (partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n") +
                "Content-Type: video/mp4\r\n" +
                "Server: SmartTV-Relay/0.9.15\r\n" +
                "Accept-Ranges: bytes\r\n" +
                "Cache-Control: no-store, no-cache\r\n" +
                "Content-Length: " + length + "\r\n";
            if (partial)
            {
                headers += "Content-Range: bytes " + start + "-" + end + "/" + VirtualLength + "\r\n";
            }
            return headers + "Connection: " + (close ? "close" : "keep-alive") + "\r\n\r\n";
        }

        private bool WaitForOffset(long offset, int seconds, out string path, out int generation)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
            lock (_sync)
            {
                while (_running && (_spoolWriter == null || _spoolPath == null || _availableLength <= offset))
                {
                    TimeSpan left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero)
                    {
                        path = null;
                        generation = _generation;
                        return false;
                    }
                    Monitor.Wait(_sync, Math.Min(500, Math.Max(1, (int)left.TotalMilliseconds)));
                }
                path = _spoolPath;
                generation = _generation;
                return _running && path != null && _availableLength > offset;
            }
        }

        private long GetAvailableLength()
        {
            lock (_sync) { return _availableLength; }
        }

        private static bool TryParseRange(string range, out bool hasRange, out long start, out long end)
        {
            hasRange = !string.IsNullOrEmpty(range);
            start = 0;
            end = VirtualLength - 1;
            if (!hasRange)
            {
                return true;
            }
            if (!range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string value = range.Substring(6).Trim();
            if (value.IndexOf(',') >= 0)
            {
                return false; // multipart byte ranges are not used by WMF here
            }
            int dash = value.IndexOf('-');
            if (dash < 0)
            {
                return false;
            }
            string startText = value.Substring(0, dash).Trim();
            string endText = value.Substring(dash + 1).Trim();
            if (startText == "")
            {
                return false; // suffix range not required by the media player
            }
            if (!long.TryParse(startText, out start) || start < 0 || start >= VirtualLength)
            {
                return false;
            }
            if (endText != "")
            {
                if (!long.TryParse(endText, out end) || end < start)
                {
                    return false;
                }
                if (end >= VirtualLength)
                {
                    end = VirtualLength - 1;
                }
            }
            return true;
        }

        private static byte[] BuildVirtualTail(byte[] init, int totalLength)
        {
            if (totalLength <= 0)
            {
                return new byte[0];
            }
            if (totalLength < 8)
            {
                return new byte[totalLength];
            }

            byte[] moov = FindTopLevelBox(init, "moov");
            if (moov == null || moov.Length == 0)
            {
                moov = init;
            }
            if (moov == null)
            {
                moov = new byte[0];
            }

            // A fragmented MP4 source commonly checks the end for an mfro/mfra
            // random-access footer. There is no final footer in an endless live
            // stream, so append a valid empty mfra marker after the copied moov.
            byte[] mfra = BuildEmptyMfra();
            int payloadCapacity = totalLength - 8;
            if (moov.Length + mfra.Length > payloadCapacity)
            {
                // The normal init moov is around 1 KiB and fits easily in WMF's
                // 16 KiB probe. For an unusual oversized init, prefer the valid
                // mfra footer rather than copying a truncated MP4 box.
                moov = new byte[0];
            }

            int payloadLength = Math.Min(payloadCapacity, moov.Length + mfra.Length);
            var payload = new byte[payloadLength];
            int moovToCopy = Math.Min(moov.Length, Math.Max(0, payload.Length - mfra.Length));
            if (moovToCopy > 0)
            {
                Buffer.BlockCopy(moov, 0, payload, 0, moovToCopy);
            }
            int mfraOffset = payload.Length - mfra.Length;
            if (mfraOffset >= 0)
            {
                Buffer.BlockCopy(mfra, 0, payload, mfraOffset, mfra.Length);
            }

            int freeSize = totalLength - payload.Length;
            var tail = new byte[totalLength];
            WriteUInt32BigEndian(tail, 0, (uint)freeSize);
            tail[4] = (byte)'f';
            tail[5] = (byte)'r';
            tail[6] = (byte)'e';
            tail[7] = (byte)'e';
            if (payload.Length > 0)
            {
                Buffer.BlockCopy(payload, 0, tail, freeSize, payload.Length);
            }
            return tail;
        }

        private static byte[] FindTopLevelBox(byte[] data, string wanted)
        {
            if (data == null || data.Length < 8)
            {
                return null;
            }
            int offset = 0;
            while (offset + 8 <= data.Length)
            {
                long size = ((long)data[offset] << 24) |
                    ((long)data[offset + 1] << 16) |
                    ((long)data[offset + 2] << 8) |
                    data[offset + 3];
                int headerSize = 8;
                if (size == 1)
                {
                    if (offset + 16 > data.Length) return null;
                    size = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        size = (size << 8) | data[offset + 8 + i];
                    }
                    headerSize = 16;
                }
                if (size == 0)
                {
                    size = data.Length - offset;
                }
                if (size < headerSize || offset + size > data.Length)
                {
                    return null;
                }
                string type = Encoding.ASCII.GetString(data, offset + 4, 4);
                if (type == wanted)
                {
                    var box = new byte[(int)size];
                    Buffer.BlockCopy(data, offset, box, 0, (int)size);
                    return box;
                }
                offset += (int)size;
            }
            return null;
        }

        private static byte[] BuildEmptyMfra()
        {
            var box = new byte[24];
            WriteUInt32BigEndian(box, 0, 24);
            box[4] = (byte)'m'; box[5] = (byte)'f'; box[6] = (byte)'r'; box[7] = (byte)'a';
            WriteUInt32BigEndian(box, 8, 16);
            box[12] = (byte)'m'; box[13] = (byte)'f'; box[14] = (byte)'r'; box[15] = (byte)'o';
            // bytes 16..19 are FullBox version/flags = 0.
            WriteUInt32BigEndian(box, 20, 24);
            return box;
        }

        private static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static string HeaderValue(string request, string name)
        {
            string prefix = name + ":";
            string[] lines = request.Split(new[] { "\r\n" }, StringSplitOptions.None);
            foreach (string line in lines)
            {
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(prefix.Length).Trim();
                }
            }
            return "";
        }

        private static void WriteAscii(Stream stream, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        private void CloseSpoolNoLock()
        {
            if (_spoolWriter != null)
            {
                try { _spoolWriter.Close(); } catch { }
                _spoolWriter = null;
            }
        }

        private static void CloseSockets(List<TcpClient> sockets)
        {
            foreach (TcpClient socket in sockets)
            {
                try { socket.Close(); } catch { }
            }
        }

        private static void TryDeletePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { File.Delete(path); } catch { }
        }
    }

}
