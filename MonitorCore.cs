using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexIdleHibernate
{
    public static class CallerContext
    {
        public static string Resolve(string directory, string threadId, string sessionId, string codexHome)
        {
            string config = Path.Combine(directory, "context.json");
            if (File.Exists(config)) return Json.Text(Json.Map(Json.Decode(File.ReadAllText(config))), "callerThreadId");
            if (!String.IsNullOrWhiteSpace(threadId)) return threadId;
            if (!String.IsNullOrWhiteSpace(sessionId)) return sessionId;
            if (String.IsNullOrWhiteSpace(codexHome))
                codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            string index = Path.Combine(codexHome, "session_index.jsonl");
            string latest = null;
            if (File.Exists(index))
            {
                using (var stream = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        // The index can be appended while read. Use its last complete ID only as call context.
                        // Busy/idle decisions always come from the live desktop pipe.
                        try
                        {
                            var row = Json.Map(Json.Decode(line));
                            object id;
                            if (row.TryGetValue("id", out id) && id is string && !String.IsNullOrWhiteSpace((string)id)) latest = (string)id;
                        }
                        catch (ArgumentException) { }
                        catch (InvalidDataException) { }
                        catch (InvalidOperationException) { }
                    }
                }
            }
            if (latest == null) throw new InvalidDataException("未找到 Codex 会话。请先在 Codex 中创建会话，或按说明配置 context.json。");
            return latest;
        }
    }

    public static class Json
    {
        public static string Encode(object value) { return new JavaScriptSerializer { MaxJsonLength = 8388608 }.Serialize(value); }
        public static object Decode(string value) { return new JavaScriptSerializer { MaxJsonLength = 8388608 }.DeserializeObject(value); }
        public static IDictionary<string, object> Map(object value)
        {
            var result = value as IDictionary<string, object>;
            if (result == null) throw new InvalidDataException("响应中应为 JSON 对象。");
            return result;
        }
        public static object Get(IDictionary<string, object> map, string key)
        {
            object result;
            if (!map.TryGetValue(key, out result)) throw new InvalidDataException("响应缺少字段：" + key);
            return result;
        }
        public static string Text(IDictionary<string, object> map, string key)
        {
            var result = Get(map, key) as string;
            if (String.IsNullOrWhiteSpace(result)) throw new InvalidDataException("响应字段无效：" + key);
            return result;
        }
        public static IList List(IDictionary<string, object> map, string key)
        {
            var result = Get(map, key) as IList;
            if (result == null) throw new InvalidDataException("响应中应为列表：" + key);
            return result;
        }
    }

    public sealed class ChatState
    {
        public string Id, Title, Host, Status;
        public bool IsIdle { get { return Status == "idle" || Status == "notLoaded"; } }
        public bool IsActive { get { return Status == "active"; } }
    }

    public sealed class Snapshot
    {
        public DateTime CheckedUtc = DateTime.UtcNow;
        public readonly List<ChatState> Chats = new List<ChatState>();
        public int ActiveCount { get { return Chats.Count(x => x.IsActive); } }
        public bool AllIdle { get { return Chats.Count > 0 && Chats.All(x => x.IsIdle); } }
        public string Description
        {
            get
            {
                if (Chats.Count == 0) return "未取得本机 Codex 会话，继续等待。";
                if (Chats.Any(x => !x.IsIdle && !x.IsActive)) return "存在无法识别的会话状态，继续等待。";
                return ActiveCount > 0 ? "仍有 " + ActiveCount + " 个会话正在运行。" : "本机 Codex 会话全部空闲。";
            }
        }
        public object Record()
        {
            return new { checkedUtc = CheckedUtc.ToString("o"), allIdle = AllIdle, active = ActiveCount,
                chats = Chats.Select(x => new { id = x.Id, title = x.Title, host = x.Host, status = x.Status }).ToArray() };
        }
    }

    public static class PipeRpc
    {
        public const int MaximumFrame = 8388608;
        public static async Task ReadExactlyAsync(Stream stream, byte[] bytes, CancellationToken token)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int n = await stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false);
                if (n == 0) throw new EndOfStreamException("Codex 连接提前关闭。");
                offset += n;
            }
        }
        public static async Task<IDictionary<string, object>> ReadFrameAsync(Stream stream, CancellationToken token)
        {
            var header = new byte[4];
            await ReadExactlyAsync(stream, header, token).ConfigureAwait(false);
            uint length = BitConverter.ToUInt32(header, 0);
            if (length == 0 || length > MaximumFrame) throw new InvalidDataException("Codex 消息长度无效。");
            var body = new byte[(int)length];
            await ReadExactlyAsync(stream, body, token).ConfigureAwait(false);
            return Json.Map(Json.Decode(new UTF8Encoding(false, true).GetString(body)));
        }
        public static IDictionary<string, object> ToolPayload(IDictionary<string, object> response, string id)
        {
            if (Json.Text(response, "jsonrpc") != "2.0" || Json.Text(response, "id") != id)
                throw new InvalidDataException("Codex 响应标识不匹配。");
            if (response.ContainsKey("error")) throw new InvalidDataException("Codex 拒绝查询：" + Json.Encode(response["error"]));
            var result = Json.Map(Json.Get(response, "result"));
            if (!Object.Equals(Json.Get(result, "success"), true)) throw new InvalidDataException("Codex 会话查询失败。");
            var items = Json.List(result, "contentItems");
            if (items.Count != 1) throw new InvalidDataException("Codex 返回了未知格式。");
            var item = Json.Map(items[0]);
            if (Json.Text(item, "type") != "inputText") throw new InvalidDataException("Codex 返回了未知类型。");
            return Json.Map(Json.Decode(Json.Text(item, "text")));
        }
        public static async Task<IDictionary<string, object>> CallAsync(string pipePath, string callerId,
            IDictionary<string, object> arguments, CancellationToken token)
        {
            const string prefix = @"\\.\pipe\";
            if (!pipePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("本地管道路径无效。");
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var stream = new NamedPipeClientStream(".", pipePath.Substring(prefix.Length), PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                timeout.CancelAfter(12000);
                using (timeout.Token.Register(() => stream.Dispose()))
                {
                    try
                    {
                        await stream.ConnectAsync(12000, timeout.Token).ConfigureAwait(false);
                        string id = Guid.NewGuid().ToString();
                        var request = new { jsonrpc = "2.0", id = id, method = "tools/call", @params = new {
                            callerSource = "codex", hostId = "local", @namespace = "codex_app", threadId = callerId,
                            turnId = "idle-monitor-read-only", callId = Guid.NewGuid().ToString(), tool = "list_threads", arguments = arguments } };
                        byte[] body = Encoding.UTF8.GetBytes(Json.Encode(request));
                        byte[] header = BitConverter.GetBytes(body.Length);
                        await stream.WriteAsync(header, 0, header.Length, timeout.Token).ConfigureAwait(false);
                        await stream.WriteAsync(body, 0, body.Length, timeout.Token).ConfigureAwait(false);
                        while (true)
                        {
                            var response = await ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false);
                            object responseId;
                            if (response.TryGetValue("id", out responseId) && Object.Equals(responseId, id))
                                return ToolPayload(response, id);
                        }
                    }
                    catch (Exception)
                    {
                        token.ThrowIfCancellationRequested();
                        if (timeout.IsCancellationRequested) throw new TimeoutException("读取 Codex 会话状态超时。");
                        throw;
                    }
                }
            }
        }
    }

    public sealed class DesktopBridge
    {
        public readonly string CallerId;
        public DesktopBridge(string callerId)
        {
            if (String.IsNullOrWhiteSpace(callerId)) throw new InvalidDataException("未找到 Codex 会话，请按说明配置 context.json。");
            CallerId = callerId;
        }
        public static string[] DiscoverPipes()
        {
            return Directory.GetFiles(@"\\.\pipe\").Where(x => Regex.IsMatch(Path.GetFileName(x),
                @"^codex-app-tools-[0-9a-fA-F-]{36}$")).OrderBy(x => x).ToArray();
        }
        public async Task<Snapshot> QueryAsync(CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(30000);
                try
                {
                    var pipes = DiscoverPipes();
                    if (pipes.Length == 0) throw new IOException("未连接到 Codex 桌面，请保持 Codex 打开。");
                    var snapshot = new Snapshot();
                    foreach (string pipe in pipes)
                    {
                        Func<IDictionary<string, object>, CancellationToken, Task<IDictionary<string, object>>> call =
                            (args, ct) => PipeRpc.CallAsync(pipe, CallerId, args, ct);
                        snapshot.Chats.AddRange((await QueryPipeAsync(call, timeout.Token).ConfigureAwait(false)).Chats);
                    }
                    snapshot.CheckedUtc = DateTime.UtcNow;
                    return snapshot;
                }
                catch (Exception)
                {
                    token.ThrowIfCancellationRequested();
                    if (timeout.IsCancellationRequested) throw new TimeoutException("本轮会话查询超时，继续等待。");
                    throw;
                }
            }
        }
        public static ChatState ParseChat(object value)
        {
            var row = Json.Map(value);
            string host = Json.Text(row, "hostId");
            string kind = Json.Text(row, "kind");
            if (host != "local" || kind != "codex") throw new InvalidDataException("会话来源不符。");
            object title;
            string id = Json.Text(row, "id");
            return new ChatState { Id = id, Host = host, Status = Json.Text(row, "status"),
                Title = row.TryGetValue("title", out title) && title is string ? (string)title : id };
        }
        public static async Task<Snapshot> QueryPipeAsync(
            Func<IDictionary<string, object>, CancellationToken, Task<IDictionary<string, object>>> call, CancellationToken token)
        {
            var recent = await call(new Dictionary<string, object> { { "limit", 50 } }, token).ConfigureAwait(false);
            if (Convert.ToInt32(Json.Get(recent, "schemaVersion")) != 4) throw new InvalidDataException("Codex 状态接口版本已变化。");
            if (Json.List(recent, "unavailableHosts").Count != 0 || Json.List(recent, "unavailableSources").Count != 0)
                throw new InvalidDataException("Codex 部分会话来源暂不可用，继续等待。");
            var chats = new Dictionary<string, ChatState>();
            foreach (string key in new[] { "pinnedThreads", "threads" })
            foreach (object value in Json.List(recent, key))
            {
                var row = Json.Map(value);
                if (Json.Text(row, "kind") != "codex") continue;
                if (Json.Text(row, "hostId") != "local")
                    throw new InvalidDataException("检测到远程 Codex 会话；当前工具仅支持本机会话。");
                Merge(chats, ParseChat(value));
            }
            // A recent list is capped at 50. Page through the whole local history so older active chats are included.
            string cursor = null;
            var cursors = new HashSet<string>();
            int historyCount = 0;
            do
            {
                token.ThrowIfCancellationRequested();
                var args = new Dictionary<string, object> { { "mode", "history" }, { "source", "codex" },
                    { "hostId", "local" }, { "limit", 50 }, { "archived", false } };
                if (cursor != null) args.Add("cursor", cursor);
                var page = await call(args, token).ConfigureAwait(false);
                foreach (object row in Json.List(page, "threads")) { Merge(chats, ParseChat(row)); historyCount++; }
                object next = Json.Get(page, "nextCursor");
                cursor = next as string;
                if (next != null && (String.IsNullOrWhiteSpace(cursor) || !cursors.Add(cursor)))
                    throw new InvalidDataException("Codex 分页游标无效或重复。");
            } while (cursor != null);
            if (historyCount == 0) throw new InvalidDataException("未能核实本机完整会话列表。");
            var snapshot = new Snapshot();
            snapshot.Chats.AddRange(chats.Values.OrderBy(x => x.IsIdle).ThenBy(x => x.Title));
            return snapshot;
        }
        static void Merge(IDictionary<string, ChatState> chats, ChatState row)
        {
            ChatState old;
            // Never erase a busy/unknown observation with an idle observation during the same scan.
            if (!chats.TryGetValue(row.Id, out old) || old.IsIdle) chats[row.Id] = row;
        }
    }

    public sealed class MonitorEvent
    {
        public string Stage, Message;
        public Snapshot Snapshot;
        public DateTime? DeadlineUtc;
    }

    public sealed class MonitorLoop
    {
        readonly Func<CancellationToken, Task<Snapshot>> query;
        readonly Func<int, CancellationToken, Task> delay;
        readonly Func<CancellationToken, Task> action;
        readonly Action<MonitorEvent> report;
        readonly int intervalMs, countdownMs;
        public MonitorLoop(Func<CancellationToken, Task<Snapshot>> query, Func<CancellationToken, Task> action,
            Action<MonitorEvent> report, int intervalMs = 60000, int countdownMs = 30000,
            Func<int, CancellationToken, Task> delay = null)
        {
            this.query = query; this.action = action; this.report = report;
            this.intervalMs = intervalMs; this.countdownMs = countdownMs;
            this.delay = delay ?? ((ms, ct) => Task.Delay(ms, ct));
        }
        async Task<Snapshot> ReadAsync(CancellationToken token)
        {
            report(new MonitorEvent { Stage = "checking", Message = "正在读取实时会话状态……" });
            try
            {
                var result = await query(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                report(new MonitorEvent { Stage = "snapshot", Message = result.Description, Snapshot = result });
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                report(new MonitorEvent { Stage = "error", Message = error.Message });
                return null;
            }
        }
        public async Task RunAsync(CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var current = await ReadAsync(token).ConfigureAwait(false);
                if (current != null && current.AllIdle)
                {
                    report(new MonitorEvent { Stage = "countdown", Message = "全部空闲，倒计时后复查并休眠。",
                        DeadlineUtc = DateTime.UtcNow.AddMilliseconds(countdownMs) });
                    await delay(countdownMs, token).ConfigureAwait(false);
                    var final = await ReadAsync(token).ConfigureAwait(false);
                    if (final != null && final.AllIdle)
                    {
                        token.ThrowIfCancellationRequested();
                        await action(token).ConfigureAwait(false);
                        return; // One successful or failed power attempt per arming; never retry it automatically.
                    }
                }
                report(new MonitorEvent { Stage = "waiting", Message = "继续等待下一轮检查。",
                    DeadlineUtc = DateTime.UtcNow.AddMilliseconds(intervalMs) });
                await delay(intervalMs, token).ConfigureAwait(false);
            }
        }
    }
}
