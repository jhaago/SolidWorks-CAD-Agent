using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using SolidWorksCadAgent.Core.Remote;
using SolidWorksCadAgent.RemoteAgent.Platform;

namespace SolidWorksCadAgent.RemoteAgent
{
    public sealed class MainForm : Form
    {
        private readonly RemotePairingCoordinator pairing; private readonly RemoteSessionCoordinator sessions;
        private readonly Timer refresh=new Timer {Interval=500};
        private readonly TextBox secret=new TextBox {ReadOnly=true,Width=540};
        private readonly Label status=new Label {AutoSize=true},pending=new Label {AutoSize=true};
        private readonly ListBox devices=new ListBox {Width=540,Height=100,DisplayMember="DeviceName"};
        private DateTimeOffset expires; private readonly RemoteStopHotkey hotkey;
        public MainForm(RemotePairingCoordinator pairing,RemoteSessionCoordinator sessions) {
            this.pairing=pairing;this.sessions=sessions;Text="SolidWorks CAD Agent — Remote Workstation";ClientSize=new Size(610,540);Font=new Font("Segoe UI",10);
            var panel=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(18),AutoScroll=true};Controls.Add(panel);
            panel.Controls.Add(new Label {AutoSize=true,Text="Live desktop viewing and manual control. Keep this window available."});
            panel.Controls.Add(status);
            AddButton(panel,"Stop Remote",()=>{sessions.StopRemote();UpdateState();},Color.MistyRose);
            hotkey=new RemoteStopHotkey(()=>{sessions.StopRemote();UpdateState();});
            panel.Controls.Add(new Label {AutoSize=true,Text=hotkey.Registered?"Local stop shortcut: Ctrl+Alt+F12":"Ctrl+Alt+F12 is unavailable. Use Stop Remote in this window."});
            panel.Controls.Add(new Label {AutoSize=true,Text="Private HTTPS address: use the URL printed by Tailscale Serve."});
            panel.Controls.Add(new Label {AutoSize=true,Text="Loopback service: http://127.0.0.1:5079/ — setup instructions are in the bundle."});
            AddButton(panel,"Open pairing window (2 minutes)",()=>{var window=pairing.OpenPairing();secret.Text=window.Secret;expires=window.ExpiresAt;UpdateState();});
            panel.Controls.Add(secret);panel.Controls.Add(pending);
            AddButton(panel,"Accept displayed device",()=>{var id=pairing.PendingRequestId;if(id==null)throw new InvalidOperationException("No pending device request. Pair from Android first.");pairing.Approve(id);secret.Clear();UpdateDevices();});
            AddButton(panel,"Reject displayed device",()=>{var id=pairing.PendingRequestId;if(id!=null)pairing.Reject(id);secret.Clear();UpdateState();});
            panel.Controls.Add(devices);
            AddButton(panel,"Remove selected device",()=>{if(devices.SelectedItem is RemoteCredentialRecord device){pairing.Revoke(device.DeviceId);UpdateDevices();}});
            panel.Controls.Add(new Label {AutoSize=true,Text="Unlock Windows for remote viewing. Elevated/UAC screens are unsupported."});
            refresh.Tick+=(sender,args)=>UpdateState();refresh.Start();UpdateDevices();UpdateState();
        }
        private void AddButton(Control parent,string text,Action action,Color? color=null) {
            var button=new Button {Text=text,AutoSize=true,MinimumSize=new Size(260,34)};if(color.HasValue)button.BackColor=color.Value;
            button.Click+=(sender,args)=>{try{action();}catch(Exception ex){MessageBox.Show(this,ex is Contracts.Remote.RemoteProtocolException?ex.Message:"The remote operation could not complete. Check setup and restart if needed.","Remote Workstation");}UpdateState();};parent.Controls.Add(button);
        }
        private void UpdateDevices() {devices.DataSource=pairing.Devices.ToList();}
        private void UpdateState() {status.Text=sessions.LocalStatusText;if(DateTimeOffset.UtcNow>=expires)secret.Clear();pending.Text=pairing.PendingRequestId==null?"No pending device request":"Device requesting access: "+pairing.PendingDeviceName;}
        protected override void OnFormClosed(FormClosedEventArgs args) {refresh.Stop();refresh.Dispose();hotkey.Dispose();sessions.StopRemote();base.OnFormClosed(args);}
    }
}
