using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CurrencyWarsAssistant.App;

/// <summary>
/// WebView2 弹性初始化：显式指定 user data 目录（exe 旁 .WebView2 文件夹，保持
/// portable 语义），初始化失败时自动把损坏/不兼容的旧目录改名保留并重建重试。
///
/// 背景（2026-08-10 实机根因）：Edge WebView2 Runtime 升级后（151.0.4129.59 →
/// 151.0.4129.72），软件无参 EnsureCoreWebView2Async() 复用旧目录会抛
/// 0x800700AA（请求的资源在使用中）。改名重建即可自愈，无需用户手工删除。
/// </summary>
internal static class WebView2Initializer
{
    /// <summary>
    /// 初始化 WebView2（如尚未初始化）。返回是否成功。
    /// </summary>
    internal static async Task<(bool Success, Exception? Failure)> EnsureInitializedAsync(
        WebView2 browser)
    {
        if (browser.CoreWebView2 is not null)
        {
            return (true, null);
        }

        var userDataFolder = ResolveUserDataFolder(browser);
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: userDataFolder);
            await browser.EnsureCoreWebView2Async(environment);
            return (true, null);
        }
        catch (Exception firstException)
        {
            // 自愈：把可能损坏/跨内核版本不兼容的旧目录改名保留，重建后重试一次。
            System.Diagnostics.Debug.WriteLine(
                $"WebView2 首次初始化失败（{firstException.Message}），尝试隔离旧目录重建");
            if (!TryQuarantine(userDataFolder))
            {
                return (false, firstException);
            }

            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: userDataFolder);
                await browser.EnsureCoreWebView2Async(environment);
                return (true, null);
            }
            catch (Exception retryException)
            {
                // 保留首次异常信息更贴近根因（0x800700AA 等），二次失败不再掩盖。
                System.Diagnostics.Debug.WriteLine(
                    $"WebView2 初始化重试失败: {retryException.Message}");
                return (false, firstException);
            }
        }
    }

    /// <summary>
    /// user data 目录：exe 同目录下 &lt;exe文件名&gt;.WebView2（与 WebView2 默认
    /// 相对路径规则一致），确保 portable 部署语义——目录跟随软件，不落在
    /// %LOCALAPPDATA% 深处、不跨版本互相干扰。
    /// </summary>
    private static string ResolveUserDataFolder(WebView2 browser)
    {
        var applicationPath = Environment.ProcessPath;
        var exePath = string.IsNullOrEmpty(applicationPath)
            ? System.Reflection.Assembly.GetExecutingAssembly().Location
            : applicationPath;
        var exeFileName = Path.GetFileNameWithoutExtension(exePath);
        return Path.Combine(
            Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
            $"{exeFileName}.WebView2");
    }

    /// <summary>
    /// 把 user data 目录改名（.corrupt-时间戳）保留现场。返回是否成功。
    /// 目录不存在视为成功（无需隔离）；改名失败（被占用等）返回 false。
    /// </summary>
    private static bool TryQuarantine(string userDataFolder)
    {
        if (!Directory.Exists(userDataFolder))
        {
            return true;
        }

        var quarantine = $"{userDataFolder}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Move(userDataFolder, quarantine);
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                // 检查后目录被并发移除：无需隔离，视为成功。
                return true;
            }
            catch (IOException) when (attempt < 2)
            {
                // 可能瞬时被占用，加时间戳换名重试。
                quarantine = $"{userDataFolder}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}-{attempt + 1}";
            }
            catch (Exception)
            {
                return false;
            }
        }

        return false;
    }
}
