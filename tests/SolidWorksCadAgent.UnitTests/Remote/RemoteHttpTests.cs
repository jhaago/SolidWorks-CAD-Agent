using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Remote;
using SolidWorksCadAgent.Core.Remote;
using SolidWorksCadAgent.RemoteAgent.Host;

namespace SolidWorksCadAgent.UnitTests.Remote
{
    internal sealed class TestCapture : IRemoteCapture {
        public RemoteCapturedFrame Frame=new RemoteCapturedFrame { FrameId=1,DisplayGeneration=1,Width=100,Height=60,CursorX=.5,CursorY=.5,JpegBytes=new byte[]{255,216,255,217} };
        public RemoteCapturedFrame Capture()=>Frame;
    }
    [TestClass]
    public class RemoteHttpTests
    {
        private static RemoteRequest Request(string path,string token=null,string method="POST",string body="{}")=>new RemoteRequest {
            Method=method,Path="/remote/v1/"+path,Authorization=token==null?null:"Session "+token,ContentType="application/json",Body=body
        };
        [TestMethod] public void UnauthenticatedFrameAndInputAreRejected() {
            var f=new SessionFixture(); var routes=new RemoteRoutes(f.Pairing,f.Sessions,new TestCapture(),f.Clock);
            Assert.AreEqual(401,routes.Handle(Request("display/frame",method:"GET",body:"")).Status);
            Assert.AreEqual(401,routes.Handle(Request("input")).Status); Assert.AreEqual(0,f.Sink.Applied);
        }
        [TestMethod] public void PairingPollRequiresReceiptAuthorization() {
            var f=new SessionFixture(); var receipt=f.Pairing.RequestPairing(f.Pairing.OpenPairing().Secret,"other"); f.Pairing.Approve(receipt.RequestId);
            var routes=new RemoteRoutes(f.Pairing,f.Sessions,new TestCapture(),f.Clock);
            var request=Request("pair/status",body:"{\"requestId\":\""+receipt.RequestId+"\"}");
            Assert.AreEqual(401,routes.Handle(request).Status);
            request.Authorization="Receipt "+receipt.ReceiptSecret;
            Assert.AreEqual(200,routes.Handle(request).Status);
            Assert.AreEqual(401,routes.Handle(request).Status);
        }
        [TestMethod] public void NonLoopbackConfigurationIsRejected() {
            foreach(var url in new[]{"http://+:5079/","http://0.0.0.0:5079/","http://192.168.1.2:5079/","https://127.0.0.1:5079/"})
                Assert.IsFalse(RemoteRequestPolicy.IsAllowedPrefix(new Uri(url.Replace("+","example.org"))));
            Assert.IsTrue(RemoteRequestPolicy.IsAllowedPrefix(new Uri("http://127.0.0.1:5079/")));
        }
        [TestMethod] public void BodiesAndFrameResponsesAreBounded() {
            var f=new SessionFixture(); var capture=new TestCapture(); capture.Frame.CapturedAt=f.Clock.UtcNow;
            var routes=new RemoteRoutes(f.Pairing,f.Sessions,capture,f.Clock);
            Assert.AreEqual(413,routes.Handle(Request("input",f.Grant.SessionToken,body:new string('x',65537))).Status);
            Assert.AreEqual(200,routes.Handle(Request("display/frame",f.Grant.SessionToken,"GET","")).Status);
            capture.Frame.JpegBytes=new byte[2097153];
            Assert.AreEqual(503,routes.Handle(Request("display/frame",f.Grant.SessionToken,"GET","")).Status);
        }
        [TestMethod] public void FrameReportsCaptureAgeOnTheWindowsClock() {
            var f=new SessionFixture(); var capture=new TestCapture(); capture.Frame.CapturedAt=f.Clock.UtcNow-TimeSpan.FromSeconds(2);
            var routes=new RemoteRoutes(f.Pairing,f.Sessions,capture,f.Clock);
            var response=routes.Handle(Request("display/frame",f.Grant.SessionToken,"GET",""));
            Assert.AreEqual(200,response.Status);
            Assert.AreEqual(2000L,(long)JObject.Parse(response.Body)["ageAtResponseMs"]);
        }
        [TestMethod] public void ErrorsNeverEchoSecrets() {
            var f=new SessionFixture(); var routes=new RemoteRoutes(f.Pairing,f.Sessions,new TestCapture(),f.Clock);
            var response=routes.Handle(Request("session/status","private-token","GET",""));
            Assert.IsFalse(response.Body.Contains("private-token")); Assert.AreEqual(401,response.Status);
            response=routes.Handle(Request("input",body:"{\"privateSecret\":\"private-token\"}"));
            Assert.IsFalse(response.Body.Contains("private-token"));
        }
        [TestMethod] public void UnsupportedVersionAndMethodAreRejected() {
            var f=new SessionFixture(); var routes=new RemoteRoutes(f.Pairing,f.Sessions,new TestCapture(),f.Clock);
            var request=Request("session/status",f.Grant.SessionToken,"DELETE",""); Assert.AreEqual(405,routes.Handle(request).Status);
            request.Path="/remote/v2/session/status"; Assert.AreEqual(404,routes.Handle(request).Status);
            request=Request("session/heartbeat",f.Grant.SessionToken); request.ContentType="text/plain"; Assert.AreEqual(415,routes.Handle(request).Status);
        }
#if NETFRAMEWORK
        [TestMethod] public void LoopbackServerCanRestartAndRejectUnauthenticatedFrames() {
            var probe=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0); probe.Start();
            int port=((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            var prefix=new Uri("http://127.0.0.1:"+port+"/");
            var f=new SessionFixture(); var routes=new RemoteRoutes(f.Pairing,f.Sessions,new TestCapture(),f.Clock);
            for(int n=0;n<2;n++) {
                using(var server=new RemoteHttpServer(routes)) using(var client=new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler {UseProxy=false})) {
                    server.Start(prefix);
                    var response=client.GetAsync(new Uri(prefix,"remote/v1/display/frame")).GetAwaiter().GetResult();
                    Assert.AreEqual(System.Net.HttpStatusCode.Unauthorized,response.StatusCode);
                    Assert.IsTrue(response.Headers.CacheControl.NoStore);
                }
            }
        }
#endif
    }
}
