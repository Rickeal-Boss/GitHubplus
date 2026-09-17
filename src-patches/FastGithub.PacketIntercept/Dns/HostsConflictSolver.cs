using FastGithub.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.PacketIntercept.Dns
{
    /// <summary>
    /// host文件冲解决者
    /// </summary>
    [SupportedOSPlatform("windows")]
    sealed class HostsConflictSolver : IDnsConflictSolver
    {
        // [PATCH] hosts 回滚记录放在 HKLM\SOFTWARE\FastGithub 下（HKLM 非管理员不可写，
        // 普通用户无法伪造/篡改），只记录「被我们加 # 注释掉的原始行段字节」（base64）用于逐字节复原。
        private const string REGISTRY_PATH = @"SOFTWARE\FastGithub";
        private const string VALUE_PATCHED_SEGMENTS = "HostsPatchedSegments";

        private const byte HASH = 0x23;   // '#'
        private const byte SPACE = 0x20;  // ' '
        private const byte TAB = 0x09;    // '\t'
        private const byte CR = 0x0D;     // '\r'
        private const byte LF = 0x0A;     // '\n'

        private readonly FastGithubConfig fastGithubConfig;
        private readonly ILogger<HostsConflictSolver> logger;

        /// <summary>
        /// host文件冲解决者
        /// </summary>
        /// <param name="fastGithubConfig"></param>
        /// <param name="logger"></param>
        public HostsConflictSolver(
            FastGithubConfig fastGithubConfig,
            ILogger<HostsConflictSolver> logger)
        {
            this.fastGithubConfig = fastGithubConfig;
            this.logger = logger;
        }

        /// <summary>
        /// hosts 文件路径
        /// </summary>
        private static string HostsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers/etc/hosts");

        /// <summary>
        /// 解决冲突
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task SolveAsync(CancellationToken cancellationToken)
        {
            var hostsPath = HostsPath;
            if (File.Exists(hostsPath) == false)
            {
                return;
            }

            // [PATCH] 上一次若被强杀（未走到 RestoreAsync），会残留「# <原行>」。
            // 先复原残留，再做本次处理，保证每次启动都能自愈。
            await this.RestoreAsync(cancellationToken);

            // [PATCH] 纯字节处理：全程只把文件当字节流，绝不整文件解码成 string 再编码写回。
            // 上游用 StreamReader 默认解码（UTF-8）读 hosts，中国区 ANSI(GBK) hosts 会被读成
            // U+FFFD 再写回 UTF-8 —— 中文注释被永久损坏。这里只对「主机名 token」做单点解码，
            // 其余字节一律原样透传，因此编码/BOM 天然保持不变（无需记录编码信息）。
            byte[] all;
            try
            {
                all = await File.ReadAllBytesAsync(hostsPath, cancellationToken);
            }
            catch (Exception ex)
            {
                this.logger.LogError($"读取hosts文件失败，跳过冲突处理：{ex.Message}");
                return;
            }

            // 按 '\n' 切分成「行段」，每段包含它自己的行尾字节（"\r\n" 或 "\n" 原样保留）。
            var segments = SplitLines(all);
            var output = new List<byte[]>(segments.Count);
            var originalSegments = new List<byte[]>();
            var hasConflicting = false;

            foreach (var segment in segments)
            {
                if (this.IsConflictingSegment(segment))
                {
                    hasConflicting = true;
                    originalSegments.Add(segment);
                    output.Add(PrependHashSpace(segment));   // "# " + 原始行段字节（含原行尾）
                }
                else
                {
                    output.Add(segment);                     // 原样写回
                }
            }

            // 没有冲突行：绝不重写文件（保持 mtime/ACL 不变）
            if (hasConflicting == false)
            {
                return;
            }

            // [PATCH] 先把回滚记录写进 HKLM 再改文件：注册表写不进就不动 hosts（失败关闭，绝不留下无法恢复的改动）。
            if (this.TryWriteRecord(originalSegments) == false)
            {
                this.logger.LogError($"无法写入hosts回滚记录（HKLM\\{REGISTRY_PATH}），为避免改坏后无法恢复，本次不修改hosts文件。");
                return;
            }

            try
            {
                await WriteAtomicAsync(hostsPath, Concat(output), cancellationToken);
            }
            catch (Exception ex)
            {
                // 写失败：文件未被改动，撤销回滚记录，避免下次 Restore 误处理
                this.DeleteRecord();
                this.logger.LogError($"解决hosts文件冲突失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 恢复冲突
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task RestoreAsync(CancellationToken cancellationToken)
        {
            var hostsPath = HostsPath;
            if (File.Exists(hostsPath) == false)
            {
                return;
            }

            var originalSegments = this.ReadRecord();
            if (originalSegments.Count == 0)
            {
                return;
            }

            try
            {
                var current = await File.ReadAllBytesAsync(hostsPath, cancellationToken);
                var changed = false;

                // 只把我们自己注释掉的那几段还原（按字节精确匹配 "# " + 原始段），其它一个字节都不碰
                foreach (var segment in originalSegments)
                {
                    var commented = PrependHashSpace(segment);
                    var restored = ReplaceFirst(current, commented, segment);
                    if (restored != null)
                    {
                        current = restored;
                        changed = true;
                    }
                }

                if (changed)
                {
                    await WriteAtomicAsync(hostsPath, current, cancellationToken);
                }

                this.DeleteRecord();
            }
            catch (Exception ex)
            {
                this.logger.LogError($"恢复hosts文件冲突失败：{ex.Message}");
            }
        }

        /// <summary>
        /// [PATCH] 是否为冲突的行段（纯字节判定，仅对主机名 token 做单点解码）
        /// </summary>
        private bool IsConflictingSegment(byte[] segment)
        {
            var (start, end) = GetTrimmedCore(segment);

            // 空行
            if (start >= end)
            {
                return false;
            }

            // 已注释行（首字节 '#'）
            if (segment[start] == HASH)
            {
                return false;
            }

            // 以 0x20/0x09 切分 token：第 1 个是 IP，其余全部是主机名（H-4 因此天然覆盖）
            var tokens = SplitTokens(segment, start, end);
            if (tokens.Count < 2)
            {
                return false;
            }

            for (var i = 1; i < tokens.Count; i++)
            {
                var host = DecodeHostToken(segment, tokens[i].Start, tokens[i].End);
                if (host != null && this.fastGithubConfig.IsMatch(host))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// [PATCH] 原子写入：先写同目录 .tmp，再优先 File.Replace（保留目标文件原有 ACL/属性），
        /// 失败退回 File.Move 覆盖。上游直接 File.WriteAllTextAsync 覆盖原文件，
        /// 进程在中途被终止（强杀/崩溃/关机）会残留半截 hosts。
        /// </summary>
        private static async Task WriteAtomicAsync(string hostsPath, byte[] content, CancellationToken cancellationToken)
        {
            var tmpPath = hostsPath + ".tmp";
            await File.WriteAllBytesAsync(tmpPath, content, cancellationToken);
            try
            {
                // hosts 位于 System32\drivers\etc，有特定 ACL；Replace 保留目标文件安全描述符
                File.Replace(tmpPath, hostsPath, destinationBackupFileName: null);
            }
            catch (Exception)
            {
                File.Move(tmpPath, hostsPath, overwrite: true);
            }
        }

        /// <summary>
        /// [PATCH] 按 0x0A 切分成行段，每段包含自己的行尾字节
        /// </summary>
        private static List<byte[]> SplitLines(byte[] all)
        {
            var segments = new List<byte[]>();
            var start = 0;
            for (var i = 0; i < all.Length; i++)
            {
                if (all[i] == LF)
                {
                    var length = i - start + 1;
                    var segment = new byte[length];
                    Array.Copy(all, start, segment, 0, length);
                    segments.Add(segment);
                    start = i + 1;
                }
            }
            if (start < all.Length)
            {
                var length = all.Length - start;
                var segment = new byte[length];
                Array.Copy(all, start, segment, 0, length);
                segments.Add(segment);
            }
            return segments;
        }

        /// <summary>
        /// [PATCH] 取得行段的「核心字节」范围 [start, end)：先剥掉尾部行尾，再 Trim 首尾的 0x20/0x09/0x0D
        /// </summary>
        private static (int Start, int End) GetTrimmedCore(byte[] segment)
        {
            var end = segment.Length;
            if (end > 0 && segment[end - 1] == LF)
            {
                end--;
            }
            if (end > 0 && segment[end - 1] == CR)
            {
                end--;
            }

            var start = 0;
            while (start < end && IsTrimByte(segment[start]))
            {
                start++;
            }
            while (end > start && IsTrimByte(segment[end - 1]))
            {
                end--;
            }
            return (start, end);
        }

        private static bool IsTrimByte(byte value)
        {
            return value == SPACE || value == TAB || value == CR;
        }

        private static bool IsSeparator(byte value)
        {
            return value == SPACE || value == TAB;
        }

        /// <summary>
        /// [PATCH] 在 [start, end) 内按 0x20/0x09 切出 token（含首尾索引）
        /// </summary>
        private static List<(int Start, int End)> SplitTokens(byte[] segment, int start, int end)
        {
            var tokens = new List<(int Start, int End)>();
            var i = start;
            while (i < end)
            {
                while (i < end && IsSeparator(segment[i]))
                {
                    i++;
                }
                if (i >= end)
                {
                    break;
                }

                var tokenStart = i;
                while (i < end && IsSeparator(segment[i]) == false)
                {
                    i++;
                }
                tokens.Add((tokenStart, i - 1));
            }
            return tokens;
        }

        /// <summary>
        /// [PATCH] 解码主机名 token（含首尾索引）：含 0x80 以上字节的 token 直接判为不匹配。
        /// 主机名是 ASCII，Latin-1 对 &lt;0x80 的字节与 ASCII 结果一致，且保证 1:1 不丢字节。
        /// </summary>
        private static string? DecodeHostToken(byte[] segment, int start, int end)
        {
            for (var i = start; i <= end; i++)
            {
                if (segment[i] >= 0x80)
                {
                    return null;
                }
            }
            return Encoding.Latin1.GetString(segment, start, end - start + 1);
        }

        /// <summary>
        /// [PATCH] "# " + 原始行段字节
        /// </summary>
        private static byte[] PrependHashSpace(byte[] segment)
        {
            var result = new byte[segment.Length + 2];
            result[0] = HASH;
            result[1] = SPACE;
            Array.Copy(segment, 0, result, 2, segment.Length);
            return result;
        }

        private static byte[] Concat(IReadOnlyList<byte[]> segments)
        {
            var total = 0;
            for (var i = 0; i < segments.Count; i++)
            {
                total += segments[i].Length;
            }

            var result = new byte[total];
            var offset = 0;
            for (var i = 0; i < segments.Count; i++)
            {
                Array.Copy(segments[i], 0, result, offset, segments[i].Length);
                offset += segments[i].Length;
            }
            return result;
        }

        /// <summary>
        /// [PATCH] 把 haystack 中第一次出现的 needle 替换为 replacement；未找到返回 null
        /// </summary>
        private static byte[]? ReplaceFirst(byte[] haystack, byte[] needle, byte[] replacement)
        {
            var index = IndexOf(haystack, needle);
            if (index < 0)
            {
                return null;
            }

            var result = new byte[haystack.Length - needle.Length + replacement.Length];
            Array.Copy(haystack, 0, result, 0, index);
            Array.Copy(replacement, 0, result, index, replacement.Length);
            Array.Copy(haystack, index + needle.Length, result, index + replacement.Length, haystack.Length - index - needle.Length);
            return result;
        }

        /// <summary>
        /// [PATCH] 子串查找（返回首次出现的下标；未找到返回 -1）
        /// </summary>
        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length)
            {
                return -1;
            }

            var limit = haystack.Length - needle.Length;
            for (var i = 0; i <= limit; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// [PATCH] 写入回滚记录（原始行段字节的 base64）到 HKLM；成功返回 true
        /// </summary>
        private bool TryWriteRecord(IReadOnlyList<byte[]> originalSegments)
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(REGISTRY_PATH, writable: true);
                if (key == null)
                {
                    return false;
                }

                // [PATCH] 纯字节处理下编码与 BOM 天然不变，无需再记录 OriginalHasBom/OriginalEncoding。
                // 仅存「被注释掉的原始行段字节」，恢复时按字节精确匹配还原。
                var payload = string.Join("\n", originalSegments.Select(Convert.ToBase64String));
                key.SetValue(VALUE_PATCHED_SEGMENTS, payload, RegistryValueKind.String);
                return true;
            }
            catch (Exception ex)
            {
                this.logger.LogError($"写入hosts回滚记录失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// [PATCH] 读取回滚记录（原始行段字节列表）
        /// </summary>
        private List<byte[]> ReadRecord()
        {
            var result = new List<byte[]>();
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(REGISTRY_PATH);
                if (key == null)
                {
                    return result;
                }

                var payload = key.GetValue(VALUE_PATCHED_SEGMENTS) as string;
                if (string.IsNullOrEmpty(payload))
                {
                    return result;
                }

                foreach (var line in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        result.Add(Convert.FromBase64String(line));
                    }
                    catch (FormatException)
                    {
                        // 单个损坏条目忽略，不影响其它条目
                    }
                }
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"读取hosts回滚记录失败：{ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// [PATCH] 删除回滚记录
        /// </summary>
        private void DeleteRecord()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(REGISTRY_PATH, writable: true);
                if (key == null)
                {
                    return;
                }

                key.DeleteValue(VALUE_PATCHED_SEGMENTS, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"删除hosts回滚记录失败：{ex.Message}");
            }
        }
    }
}
