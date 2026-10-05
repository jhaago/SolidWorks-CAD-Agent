using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SolidWorksCadAgent.RemoteAgent.Platform
{
    public sealed class RemoteStopHotkey : NativeWindow,IDisposable
    {
        private readonly Action stop;
        public bool Registered { get; }
        public RemoteStopHotkey(Action stop) {
            this.stop=stop;CreateHandle(new CreateParams {Caption="CAD Agent Remote Stop",Parent=new IntPtr(-3)});
            Registered=RegisterHotKey(Handle,1,0x0002|0x0001|0x4000,0x7B);
        }
        protected override void WndProc(ref Message message) {if(message.Msg==0x0312&&message.WParam.ToInt32()==1)stop();base.WndProc(ref message);}
        public void Dispose() {if(Registered)UnregisterHotKey(Handle,1);DestroyHandle();}
        [DllImport("user32.dll",SetLastError=true)] private static extern bool RegisterHotKey(IntPtr handle,int id,uint modifiers,uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr handle,int id);
    }
}
