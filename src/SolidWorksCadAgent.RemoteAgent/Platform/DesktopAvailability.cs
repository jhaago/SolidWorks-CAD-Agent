using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SolidWorksCadAgent.RemoteAgent.Platform
{
    internal static class DesktopAvailability
    {
        public static bool IsInteractive() {
            var desktop=OpenInputDesktop(0,false,1); if(desktop==IntPtr.Zero)return false;
            try { var name=new StringBuilder(256); int length; return GetUserObjectInformation(desktop,2,name,name.Capacity*2,out length)&&name.ToString().Equals("Default",StringComparison.OrdinalIgnoreCase); }
            finally {CloseDesktop(desktop);}
        }
        [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool GetUserObjectInformation(IntPtr handle,int index,StringBuilder data,int length,out int needed);
    }
}
