using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorksCadAgent.Contracts.Remote;
using SolidWorksCadAgent.Core.Remote;

namespace SolidWorksCadAgent.RemoteAgent.Platform
{
    public sealed class WindowsInputSink : IRemoteInputSink
    {
        private readonly object gate=new object(); private readonly WindowsDesktopCapture capture;
        private readonly HashSet<string> keys=new HashSet<string>(),buttons=new HashSet<string>();
        public WindowsInputSink(WindowsDesktopCapture capture) {this.capture=capture;}
        public bool Apply(RemoteInputEvent value) {
            lock(gate) {
                if(!value.IsValid())return false;var geometry=capture.Geometry();if(geometry.Generation!=value.DisplayGeneration)return false;
                if(value.Kind=="keyDown"||value.Kind=="keyUp") {
                    ushort code=KeyCode(value.Key);bool down=value.Kind=="keyDown";
                    if(!down&&!keys.Contains(value.Key))return true;
                    if(!Keyboard(code,down))return false;
                    if(down)keys.Add(value.Key);else keys.Remove(value.Key);return true;
                }
                var point=DesktopCoordinateMapper.Map(value.X,value.Y,geometry.Monitor,geometry.VirtualDesktop);
                if(!Mouse(0x8000|0x4000|1,point.X,point.Y,0))return false;
                if(value.Kind=="move")return true;
                if(value.Kind=="scroll")return Mouse(0x0800,0,0,unchecked((uint)value.Scroll));
                if(value.Kind=="up")return Button(value.Button,false);
                if(value.Kind=="down")return Button(value.Button,true);
                return Button(value.Button,true)&&Button(value.Button,false);
            }
        }
        private bool Button(string button,bool down) {
            if(!down&&!buttons.Contains(button))return true;
            uint flag=button=="primary"?(down?2u:4u):(down?8u:16u);
            if(!Mouse(flag,0,0,0))return false;
            if(down)buttons.Add(button);else buttons.Remove(button);return true;
        }
        public bool ReleaseAll() {
            lock(gate) {
                bool success=true;
                foreach(var key in keys.ToArray()) {if(Keyboard(KeyCode(key),false))keys.Remove(key);else success=false;}
                foreach(var button in buttons.ToArray()) {if(!Button(button,false))success=false;}
                return success;
            }
        }
        private static ushort KeyCode(string key) {
            if(key.Length==1)return (ushort)key[0];
            if(key[0]=='F'&&int.TryParse(key.Substring(1),out int number))return (ushort)(111+number);
            switch(key) {
                case "Backspace":return 8;case "Tab":return 9;case "Enter":return 13;case "Shift":return 16;case "Control":return 17;case "Alt":return 18;
                case "Escape":return 27;case "Space":return 32;case "PageUp":return 33;case "PageDown":return 34;case "End":return 35;case "Home":return 36;
                case "Left":return 37;case "Up":return 38;case "Right":return 39;case "Down":return 40;default:throw new ArgumentException("Unsupported remote key.");
            }
        }
        private static bool Mouse(uint flags,int x,int y,uint data)=>Send(new NativeInput {Type=0,Data=new InputUnion {Mouse=new MouseInput {Dx=x,Dy=y,Flags=flags,MouseData=data}}});
        private static bool Keyboard(ushort code,bool down)=>Send(new NativeInput {Type=1,Data=new InputUnion {Keyboard=new KeyboardInput {VirtualKey=code,Flags=(down?0u:2u)|((code>=33&&code<=40)?1u:0u)}}});
        private static bool Send(NativeInput value)=>SendInput(1,new[]{value},Marshal.SizeOf(typeof(NativeInput)))==1;
        [StructLayout(LayoutKind.Sequential)] private struct NativeInput {public uint Type;public InputUnion Data;}
        [StructLayout(LayoutKind.Explicit)] private struct InputUnion {[FieldOffset(0)]public MouseInput Mouse;[FieldOffset(0)]public KeyboardInput Keyboard;}
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput {public int Dx,Dy;public uint MouseData,Flags,Time;public IntPtr ExtraInfo;}
        [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput {public ushort VirtualKey,Scan;public uint Flags,Time;public IntPtr ExtraInfo;}
        [DllImport("user32.dll",SetLastError=true)] private static extern uint SendInput(uint count,NativeInput[] values,int size);
    }
}
