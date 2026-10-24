using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MyFirstMafAgent.Api.Plugins.Isolation;

/// <summary>
/// Windows AppContainer 的最小封装：建配置、拿 SID、授 ACL。
///
/// <para>
/// 为什么是 AppContainer 而不是"受限令牌"：受限令牌只能削权限（特权、SID），
/// <b>不能限制网络</b>。AppContainer 令牌是低完整性 + 能力模型：令牌里没有
/// <c>internetClient</c>（<c>S-1-15-3-1</c>）这个能力 SID 时，进程连出站 socket 都建不出来。
/// 这正是"用户没授权就真的不能联网"在技术上唯一不依赖管理员的实现方式。
/// </para>
///
/// <para>
/// 授权是<b>加法</b>：容器的 SID 不在任何既有 DACL 里，所以我们必须显式把
/// 插件目录（只读）、数据目录（可写）与沿途祖先目录（只给 traverse）加进去。
/// 祖先只给 traverse 很关键——给 ReadAndExecute 等于把整条路径上的兄弟目录一起开放。
/// </para>
/// </summary>
internal static class WindowsAppContainer
{
    private const int ErrorAlreadyExists = 183;
    private const int ErrorAccessDenied = 5;

    // ───────────────────────── 配置（profile）─────────────────────────

    /// <summary>
    /// 创建（或取回）AppContainer 配置，返回它的 SID 字符串。
    /// 配置名已存在时走派生 SID，不算失败。
    /// </summary>
    public static string EnsureProfile(string containerName, out bool created)
    {
        created = false;

        var hr = Native.CreateAppContainerProfile(containerName, containerName, "MyFirstMafAgent 插件沙箱", IntPtr.Zero, 0, out var sid);

        if (hr < 0)
        {
            // 有的 API 返回 HRESULT_FROM_WIN32，有的直接返回 Win32 码，两种都归一化。
            var code = (hr & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? (hr & 0xFFFF) : hr;

            // 已经建过（重装、重启、多次启动）：直接派生 SID。
            if (code == ErrorAlreadyExists)
            {
                var derived = Native.DeriveAppContainerSidFromAppContainerName(containerName, out sid);
                if (derived < 0)
                    throw new Win32Exception(derived & 0xFFFF, $"无法派生 AppContainer '{containerName}' 的 SID");
            }
            else
            {
                throw new Win32Exception(code, $"创建 AppContainer '{containerName}' 失败（HRESULT 0x{hr:X8}）");
            }
        }
        else
        {
            created = true;
        }

        try
        {
            if (!Native.ConvertSidToStringSid(sid, out var sidStringPointer))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法把 AppContainer SID 转成字符串");

            try { return Marshal.PtrToStringUni(sidStringPointer) ?? throw new Win32Exception("SID 转换结果为空"); }
            finally { Native.LocalFree(sidStringPointer); }
        }
        finally
        {
            if (sid != IntPtr.Zero) Native.FreeSid(sid);
        }
    }

    /// <summary>删除配置（卸载插件时可选）。失败只记日志，由调用方决定。</summary>
    public static bool TryDeleteProfile(string containerName)
    {
        try
        {
            var hr = Native.DeleteAppContainerProfile(containerName);
            return hr >= 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    // ───────────────────────── ACL ─────────────────────────

    /// <summary>
    /// 按计划把权限授给容器 SID。返回"真正授了哪些路径"（目录不存在就跳过）。
    /// <para>
    /// 已经授过的目录<b>不再写一次 ACL</b>：<c>SetAccessControl</c> 会让 Windows 递归重整
    /// 该目录下的继承，插件每次启动都走一遍既慢又危险（在驱动器根上实测会卡死数分钟）。
    /// </para>
    /// <para>
    /// 任何授权失败都向上抛——少一条授权，启动后的报错会变成看不懂的 access denied。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ApplyGrants(string containerSid, IReadOnlyList<PluginIsolationGrant> grants)
    {
        var sid = new SecurityIdentifier(containerSid);
        var applied = new List<string>();

        foreach (var grant in grants)
        {
            if (!Directory.Exists(grant.Path)) continue;

            var (rights, inheritance) = grant.Kind switch
            {
                PluginIsolationGrantKinds.Modify => (FileSystemRights.Modify,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit),
                PluginIsolationGrantKinds.ReadExecute => (FileSystemRights.ReadAndExecute,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit),
                // 祖先只放行"穿过"，不列目录、不读文件，也不向下继承。
                _ => (FileSystemRights.Traverse, InheritanceFlags.None)
            };

            var directory = new DirectoryInfo(grant.Path);
            var security = directory.GetAccessControl(AccessControlSections.Access);

            if (HasEquivalentRule(security, sid, rights, inheritance)) continue;

            var rule = new FileSystemAccessRule(
                sid,
                rights,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow);

            security.AddAccessRule(rule);
            directory.SetAccessControl(security);
            applied.Add(grant.Path);
        }

        return applied;
    }

    /// <summary>判断是否已经存在一条至少覆盖 <paramref name="rights"/> 与继承范围的 Allow 规则。</summary>
    private static bool HasEquivalentRule(
        DirectorySecurity security,
        SecurityIdentifier sid,
        FileSystemRights rights,
        InheritanceFlags inheritance)
    {
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference is not SecurityIdentifier identity) continue;
            if (!identity.Equals(sid)) continue;
            if ((rule.FileSystemRights & rights) != rights) continue;
            if ((rule.InheritanceFlags & inheritance) != inheritance) continue;

            return true;
        }

        return false;
    }

    /// <summary>
    /// 判断某个文件/目录对容器是否可读：只看这个对象<b>自己的有效 DACL</b>
    /// （显式 + 已继承），里面是否有给容器 SID 或 ALL APPLICATION PACKAGES 的读权限。
    ///
    /// <para>
    /// 这个检查的意义是<b>把失败提前</b>：运行时（node.exe、dotnet.exe）装在
    /// Program Files 时通常只授了 <c>Users</c>，容器读不到。实测
    /// <c>C:\Program Files\nodejs\node.exe</c> 就是拒绝访问，而授过权的目录（哪怕在
    /// 用户目录、含中文路径）完全可读。<b>穿越祖先目录不需要额外授权</b>，
    /// 只有目标对象本身需要。
    /// </para>
    /// <para>
    /// 不要改成"沿祖先找可继承的 ACE"：<c>C:\Program Files</c> 上确实有一条
    /// <c>ALL APPLICATION PACKAGES:(OI)(CI)(RX)</c>，但 <c>nodejs</c> 这一层把继承断掉了，
    /// 于是祖先看起来可读、实际读不到——这正是实测踩到的坑。
    /// </para>
    /// </summary>
    public static bool IsReadableByContainer(string path, string containerSid)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var sid = new SecurityIdentifier(containerSid);
        var everyone = new SecurityIdentifier(PluginIsolationPlanner.AllApplicationPackagesSid);

        return GrantsRead(path, sid, everyone, AccessControlSections.Access);
    }

    private static bool GrantsRead(string path, SecurityIdentifier containerSid, SecurityIdentifier everyone, AccessControlSections sections)
    {
        try
        {
            var entries = new FileInfo(path).Exists
                ? new FileInfo(path).GetAccessControl(sections).GetAccessRules(true, true, typeof(SecurityIdentifier))
                : new DirectoryInfo(path).GetAccessControl(sections).GetAccessRules(true, true, typeof(SecurityIdentifier));

            foreach (FileSystemAccessRule rule in entries)
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if (rule.IdentityReference is not SecurityIdentifier identity) continue;
                if (!identity.Equals(containerSid) && !identity.Equals(everyone)) continue;
                if ((rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute) return true;
            }

            return false;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>把 Win32 错误码翻译成用户能照着做的建议。</summary>
    public static string ExplainGrantFailure(string path, int errorCode) => errorCode switch
    {
        ErrorAccessDenied =>
            $"当前用户无权修改 '{path}' 的访问控制列表。插件运行时若在该目录（例如 Program Files 下的 nodejs），"
            + "请把它复制到插件运行时目录（plugins/.runtimes）或插件包内自带运行时。",
        _ => $"给 '{path}' 授权失败（Win32 错误 {errorCode}）"
    };

    private static class Native
    {
        [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int CreateAppContainerProfile(
            string pszAppContainerName,
            string pszDisplayName,
            string pszDescription,
            IntPtr pCapabilities,
            int dwCapabilityCount,
            out IntPtr ppSidAppContainerSid);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int DeleteAppContainerProfile(string pszAppContainerName);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int DeriveAppContainerSidFromAppContainerName(string pszAppContainerName, out IntPtr ppsidAppContainerSid);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr LocalFree(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern IntPtr FreeSid(IntPtr pSid);
    }
}
