using System;
using System.IO;
using System.Net;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace HoloLensCaptureDemo.Editor
{
    [InitializeOnLoad]
    public static class DemoCaptureSmokeTest
    {
        private const string Flag = "HoloLensCaptureDemo.CaptureSmoke";
        private static int stage;
        private static double started, nextTick, waitUntil;
        private static CaptureController controller;
        private static string previewId, leaseId;
        [Serializable] private sealed class Request { public string previewId, action, deviceAddress = "127.0.0.1", pausedLeaseId; }
        [Serializable] private sealed class Reply { public string action, leaseId; }
        static DemoCaptureSmokeTest() { EditorApplication.playModeStateChanged += OnPlay; }
        public static void Begin()
        {
            Environment.SetEnvironmentVariable("HOLOLENS_DEMO_OUTBOX_DIR", Path.Combine(DemoBuild.Root,"logs/capture-smoke-"+Guid.NewGuid().ToString("N"),"photos"));
            Environment.SetEnvironmentVariable("HOLOLENS_DEMO_SERVER_URL", "http://127.0.0.1:8769");
            Environment.SetEnvironmentVariable("HOLOLENS_DEMO_API_TOKEN", "history-test-token-not-production");
            SessionState.SetBool(Flag, true); EditorApplication.isPlaying = true;
        }
        private static void OnPlay(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(Flag,false) || state != PlayModeStateChange.EnteredPlayMode) return;
            stage=0; started=EditorApplication.timeSinceStartup; nextTick=0; previewId=Guid.NewGuid().ToString("N");
            EditorApplication.update += Tick;
        }
        private static Reply Poll(string action="poll", string paused=null)
        {
            var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:8769/api/live-preview");
            request.Method="POST"; request.Timeout=2000; request.Proxy=null; request.ContentType="application/json";
            request.Headers["X-Demo-Action"]="live-preview";
            byte[] bytes=Encoding.UTF8.GetBytes(JsonUtility.ToJson(new Request { previewId=previewId,action=action,pausedLeaseId=paused }));
            using(var stream=request.GetRequestStream()) stream.Write(bytes,0,bytes.Length);
            using(var response=request.GetResponse()) using(var reader=new StreamReader(response.GetResponseStream()))
                return JsonUtility.FromJson<Reply>(reader.ReadToEnd());
        }
        private static void Require(bool condition,string detail) { if(!condition) throw new Exception(detail); }
        private static void Tick()
        {
            double now=EditorApplication.timeSinceStartup;
            if(now<nextTick)return; nextTick=now+.1;
            try
            {
                if(now-started>60)throw new Exception("Capture smoke timeout at "+stage);
                if(controller==null)controller=UnityEngine.Object.FindObjectOfType<CaptureController>();
                if(controller==null||!controller.IsReady)return;
                if(stage<5 && controller.LastError!=null)throw new Exception(controller.LastError);
                if(stage==0)
                {
                    Poll("register"); controller.SelectPressed(71); controller.SelectPressed(71); stage=1;
                }
                else if(stage==1)
                {
                    var state=Poll(); if(state.action!="pause")return;
                    Require(controller.Labels.Count==0 && controller.PendingCount==0,"Captured before video paused.");
                    leaseId=state.leaseId; waitUntil=now+1; stage=2;
                }
                else if(stage==2)
                {
                    Poll(); controller.SelectPressed(71); controller.SelectPressed(72);
                    Require(controller.Labels.Count==0,"Hold or busy press bypassed video pause.");
                    if(now<waitUntil)return; Poll(paused:leaseId); stage=3;
                }
                else if(stage==3)
                {
                    if(controller.Labels.Count!=1 || Poll().action!="play")return;
                    controller.SelectPressed(71); controller.SelectPressed(72);
                    Require(!controller.IsCapturing,"Held/busy press caused a delayed second capture.");
                    waitUntil=now+.5; stage=4;
                }
                else if(stage==4)
                {
                    Poll(); if(now<waitUntil)return;
                    Require(controller.Labels.Count==1,"One press produced multiple labels.");
                    controller.SelectReleased(71); controller.SelectPressed(71); stage=5;
                }
                else if(stage==5)
                {
                    var state=Poll(); if(state.action=="pause")Poll(paused:state.leaseId);
                    if(controller.Labels.Count==2 && state.action=="play")
                    { controller.SelectReleased(71); controller.SelectPressed(71); stage=6; }
                }
                else if(stage==6)
                {
                    // Never acknowledge this pause: the shutter must fail closed,
                    // release its lease, and leave the two existing photos intact.
                    var state=Poll();
                    if(controller.LastError==null || state.action!="play")return;
                    Require(controller.Labels.Count==2 && controller.LastError.Contains("暂停"),"Pause timeout did not fail safely.");
                    Poll("stop"); Finish(true,"Press edge, held/busy deduplication, pause ACK before capture, automatic resume, second press, and timeout release passed.");
                }
            }
            catch(Exception error){Finish(false,error.ToString());}
        }
        private static void Finish(bool success,string message)
        {
            EditorApplication.update-=Tick; SessionState.SetBool(Flag,false);
            File.WriteAllText(Path.Combine(DemoBuild.Root,"logs/capture-smoke.json"),"{\"passed\":"+(success?"true":"false")+",\"stage\":"+stage+",\"message\":"+JsonUtility.ToJson(new Message{text=message})+"}");
            Debug.Log("CAPTURE_DEMO_CAPTURE_SMOKE "+success+" "+message); EditorApplication.Exit(success?0:1);
        }
        [Serializable]private sealed class Message{public string text;}
    }
}
