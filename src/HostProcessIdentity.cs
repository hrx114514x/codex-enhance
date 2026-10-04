using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexEnhance;
internal static class HostProcessIdentity
{
    [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr OpenProcess(uint access,bool inherit,uint processId);
    [DllImport("kernel32.dll",SetLastError=true,CharSet=CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder name,ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    internal static string? ImagePath(uint processId)
    {
        // Reading module lists requires VM access and fails against an elevated
        // Codex from a normal overlay. Process identity needs only limited query.
        var process=OpenProcess(0x1000,false,processId);
        if(process==IntPtr.Zero)return null;
        try {
            uint size=1024;var path=new StringBuilder((int)size);
            if(QueryFullProcessImageName(process,0,path,ref size))return path.ToString();
            if(Marshal.GetLastWin32Error()!=122)return null;
            size=32768;path=new StringBuilder((int)size);
            return QueryFullProcessImageName(process,0,path,ref size)?path.ToString():null;
        } finally {CloseHandle(process);}
    }
    internal static bool IsCodexPath(string? path)
    {
        if(string.IsNullOrEmpty(path))return false;
        path=path.Replace('/','\\');
        return path.EndsWith("\\ChatGPT.exe",StringComparison.OrdinalIgnoreCase)&&
            (path.Contains("\\OpenAI.Codex_",StringComparison.OrdinalIgnoreCase)||path.Contains("\\OpenAI.Codex\\",StringComparison.OrdinalIgnoreCase));
    }
    internal static bool IsCodex(uint processId)=>IsCodexPath(ImagePath(processId));
}
