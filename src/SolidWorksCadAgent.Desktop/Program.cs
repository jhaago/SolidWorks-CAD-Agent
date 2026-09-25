using System;
using System.Windows.Forms;
using SolidWorksCadAgent.Desktop.Api;

namespace SolidWorksCadAgent.Desktop
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var client = new AgentHostClient())
            {
                Application.Run(new MainForm(client));
            }
        }
    }
}
