using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CodexIdleHibernate
{
    static class Tests
    {
        static int passed;
        static void Assert(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            passed++; Console.WriteLine("PASS: " + name);
        }
        static async Task Reject(Func<Task> action, string name)
        {
            bool rejected = false;
            try { await action(); } catch (Exception) { rejected = true; }
            Assert(rejected, name);
        }
        static Snapshot State(string state)
        {
            var s = new Snapshot(); s.Chats.Add(new ChatState { Id = "test", Title = "测试", Host = "local", Status = state }); return s;
        }
        static object Row(string id, string status)
        {
            return new Dictionary<string, object> { { "id", id }, { "title", "会话 " + id }, { "status", status }, { "kind", "codex" }, { "hostId", "local" } };
        }
        static IDictionary<string, object> Recent(object[] rows = null)
        {
            return new Dictionary<string, object> { { "schemaVersion", 4 }, { "pinnedThreads", rows ?? new object[0] },
                { "threads", new object[0] }, { "unavailableHosts", new object[0] }, { "unavailableSources", new object[0] } };
        }
        static IDictionary<string, object> Page(object[] rows, string cursor = null)
        {
            return new Dictionary<string, object> { { "threads", rows }, { "nextCursor", cursor } };
        }
        static Task<Snapshot> Scan(params IDictionary<string, object>[] pages)
        {
            int i = 0;
            return DesktopBridge.QueryPipeAsync((args, ct) => Task.FromResult(pages[i++]), CancellationToken.None);
        }
        static async Task Run()
        {
            string contextRoot = Path.Combine(Path.GetTempPath(), "codex-idle-context-" + Guid.NewGuid());
            Directory.CreateDirectory(contextRoot);
            try
            {
                Assert(CallerContext.Resolve(contextRoot, "env-thread", null, contextRoot) == "env-thread", "caller context accepts the current process thread");
                Assert(CallerContext.Resolve(contextRoot, null, "env-session", contextRoot) == "env-session", "caller context accepts a session environment");
                File.WriteAllText(Path.Combine(contextRoot, "session_index.jsonl"), "{\"id\":\"first\"}\n{\"id\":\"latest\"}\n{\"id\":");
                Assert(CallerContext.Resolve(contextRoot, null, null, contextRoot) == "latest", "standalone caller context uses last complete local session without personal config");
                File.WriteAllText(Path.Combine(contextRoot, "context.json"), "{\"callerThreadId\":\"configured\"}");
                Assert(CallerContext.Resolve(contextRoot, "env-thread", null, contextRoot) == "configured", "explicit caller configuration takes precedence");
                File.Delete(Path.Combine(contextRoot, "context.json"));
                File.Delete(Path.Combine(contextRoot, "session_index.jsonl"));
                await Reject(() => { CallerContext.Resolve(contextRoot, null, null, contextRoot); return Task.FromResult(true); }, "missing local context fails with setup instructions");
            }
            finally { Directory.Delete(contextRoot, true); }
            Assert(!new Snapshot().AllIdle && !State("future-status").AllIdle && !State("active").AllIdle,
                "empty / unknown / active status cannot hibernate");
            Assert(State("idle").AllIdle && State("notLoaded").AllIdle, "known idle runtime states accepted");
            var scan = await Scan(Recent(new[] { Row("same", "active") }), Page(new[] { Row("same", "idle") }));
            Assert(!scan.AllIdle && scan.ActiveCount == 1, "busy observation survives a later idle row in same scan");
            int pageIndex = 0;
            var paged = await DesktopBridge.QueryPipeAsync((args, ct) => {
                pageIndex++;
                if (pageIndex == 1) return Task.FromResult(Recent());
                Assert(Object.Equals(args["limit"], 50) && Object.Equals(args["archived"], false), "full history page request");
                if (pageIndex == 2) return Task.FromResult(Page(Enumerable.Range(0, 50).Select(i => Row(i.ToString(), "idle")).ToArray(), "page2"));
                Assert(Object.Equals(args["cursor"], "page2"), "cursor passed to next page");
                return Task.FromResult(Page(new[] { Row("older-active", "active") }));
            }, CancellationToken.None);
            Assert(paged.Chats.Count == 51 && paged.ActiveCount == 1, "active chat beyond first 50 is detected");
            var unavailable = Recent(); unavailable["unavailableHosts"] = new object[] { "offline" };
            await Reject(async () => { await Scan(unavailable); }, "unavailable host blocks decision");
            unavailable = Recent(); unavailable["unavailableSources"] = new object[] { "chatgpt" };
            await Reject(async () => { await Scan(unavailable); }, "unavailable source blocks decision");
            var broken = Recent(); broken.Remove("threads");
            await Reject(async () => { await Scan(broken); }, "missing list blocks decision");
            var remote = (IDictionary<string, object>)Row("remote", "idle"); remote["hostId"] = "ssh";
            await Reject(async () => { await Scan(Recent(new object[] { remote })); }, "remote host blocks local-only utility");
            await Reject(async () => { await Scan(Recent(), Page(new object[0])); }, "empty history cannot hibernate");
            await Reject(async () => { await Scan(Recent(), Page(new[] { Row("a", "idle") }, "repeat"), Page(new object[0], "repeat")); },
                "repeated history cursor rejected");

            var delays = new List<int>(); int reads = 0, actions = 0;
            var states = new[] { State("active"), State("idle"), State("idle") };
            var loop = new MonitorLoop(ct => Task.FromResult(states[reads++]), ct => { actions++; return Task.FromResult(true); }, e => { },
                delay: (ms, ct) => { delays.Add(ms); ct.ThrowIfCancellationRequested(); return Task.FromResult(true); });
            await loop.RunAsync(CancellationToken.None);
            Assert(reads == 3 && actions == 1 && delays.SequenceEqual(new[] { 60000, 30000 }), "busy to idle: 60-second interval, countdown, fresh recheck, one action");

            delays.Clear(); reads = actions = 0;
            states = new[] { State("idle"), State("active"), State("idle"), State("idle") };
            loop = new MonitorLoop(ct => Task.FromResult(states[reads++]), ct => { actions++; return Task.FromResult(true); }, e => { },
                delay: (ms, ct) => { delays.Add(ms); return Task.FromResult(true); });
            await loop.RunAsync(CancellationToken.None);
            Assert(reads == 4 && actions == 1 && delays.SequenceEqual(new[] { 30000, 60000, 30000 }), "new activity at final recheck prevents initial hibernation");

            delays.Clear(); reads = actions = 0;
            loop = new MonitorLoop(ct => { reads++; if (reads < 3) throw new IOException("offline"); return Task.FromResult(State("idle")); },
                ct => { actions++; return Task.FromResult(true); }, e => { },
                delay: (ms, ct) => { delays.Add(ms); return Task.FromResult(true); });
            await loop.RunAsync(CancellationToken.None);
            Assert(actions == 1 && reads == 4 && delays.SequenceEqual(new[] { 60000, 60000, 30000 }), "query errors wait until two successful idle checks");

            using (var cancel = new CancellationTokenSource())
            {
                actions = 0;
                loop = new MonitorLoop(ct => Task.FromResult(State("idle")), ct => { actions++; return Task.FromResult(true); }, e => { },
                    delay: (ms, ct) => { cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(true); });
                await Reject(async () => await loop.RunAsync(cancel.Token), "countdown cancellation stops loop");
                Assert(actions == 0, "cancelled countdown performs no action");
            }
            using (var cancel = new CancellationTokenSource())
            {
                reads = actions = 0;
                loop = new MonitorLoop(ct => { reads++; if (reads == 2) cancel.Cancel(); return Task.FromResult(State("idle")); },
                    ct => { ct.ThrowIfCancellationRequested(); actions++; return Task.FromResult(true); }, e => { },
                    delay: (ms, ct) => Task.FromResult(true));
                await Reject(async () => await loop.RunAsync(cancel.Token), "cancel during final query stops loop");
                Assert(actions == 0, "no commit after cancellation at final check");
            }
            actions = 0;
            loop = new MonitorLoop(ct => Task.FromResult(State("idle")), ct => { actions++; throw new IOException("power failure"); }, e => { },
                delay: (ms, ct) => Task.FromResult(true));
            await Reject(async () => await loop.RunAsync(CancellationToken.None), "power failure stops monitoring");
            Assert(actions == 1, "failed power command is never retried automatically");

            await Reject(async () => { await PipeRpc.ReadFrameAsync(new MemoryStream(new byte[] { 10, 0, 0, 0, 1, 2 }), CancellationToken.None); },
                "truncated frame rejected");
            await Reject(async () => { await PipeRpc.ReadFrameAsync(new MemoryStream(new byte[] { 255, 255, 255, 255 }), CancellationToken.None); },
                "oversized frame rejected before allocation");
            await Reject(async () => { await PipeRpc.ReadFrameAsync(new MemoryStream(new byte[] { 1, 0, 0, 0, 123 }), CancellationToken.None); },
                "malformed JSON rejected");
            await Reject(() => { PipeRpc.ToolPayload(Json.Map(Json.Decode("{\"jsonrpc\":\"2.0\",\"id\":\"x\",\"result\":{\"success\":false}}")), "x"); return Task.FromResult(true); },
                "tool failure cannot become an empty successful snapshot");
            await NamedPipeRoundTrip();
            await CancelPendingPipe();
        }
        static async Task NamedPipeRoundTrip()
        {
            string name = "codex-idle-test-" + Guid.NewGuid();
            using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            {
                Task accept = server.WaitForConnectionAsync();
                var client = PipeRpc.CallAsync(@"\\.\pipe\" + name, "test-thread", new Dictionary<string, object> { { "limit", 50 } }, CancellationToken.None);
                await accept;
                var request = await PipeRpc.ReadFrameAsync(server, CancellationToken.None);
                var args = Json.Map(Json.Get(request, "params"));
                Assert(Object.Equals(args["tool"], "list_threads") && Object.Equals(args["threadId"], "test-thread"), "native request uses read-only list_threads");
                var response = new { jsonrpc = "2.0", id = request["id"], result = new { success = true,
                    contentItems = new[] { new { type = "inputText", text = Json.Encode(Recent()) } } } };
                byte[] body = Encoding.UTF8.GetBytes(Json.Encode(response));
                byte[] frame = BitConverter.GetBytes(body.Length).Concat(body).ToArray();
                for (int i = 0; i < frame.Length; i += 3) await server.WriteAsync(frame, i, Math.Min(3, frame.Length - i));
                var result = await client;
                Assert(Convert.ToInt32(result["schemaVersion"]) == 4, "partial header/body reads round-trip over real Windows pipe");
            }
        }
        static async Task CancelPendingPipe()
        {
            string name = "codex-idle-test-" + Guid.NewGuid();
            using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            using (var cancel = new CancellationTokenSource())
            {
                Task accept = server.WaitForConnectionAsync();
                var client = PipeRpc.CallAsync(@"\\.\pipe\" + name, "test-thread", new Dictionary<string, object>(), cancel.Token);
                await accept; await PipeRpc.ReadFrameAsync(server, CancellationToken.None);
                var watch = System.Diagnostics.Stopwatch.StartNew(); cancel.Cancel();
                await Reject(async () => { await client; }, "cancel interrupts a pending native pipe read");
                Assert(watch.ElapsedMilliseconds < 2000, "cancellation completes without waiting for query timeout");
            }
        }
        static int Main()
        {
            try { Run().GetAwaiter().GetResult(); Console.WriteLine("All " + passed + " checks passed. No power commands executed."); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
