using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MusicBar;
internal static class NetEaseBridgeChecks
{
    static int passed;
    static void Check(bool ok,string msg){if(!ok)throw new Exception(msg);passed++;}
    static IDictionary<string,object> Sample(double p,double d,string state){return new Dictionary<string,object>{{"id","123"},{"title","Song"},{"artist","Artist"},{"album","Album"},{"position",p},{"duration",d},{"state",state}};}
    public static void Main()
    {
        var now=DateTime.UtcNow;var s=NetEaseBridgeReader.ParseSample(Sample(43.123,200.456,"Playing"),now.AddSeconds(-5));
        Check(s!=null&&s.HasTimeline&&s.IsPlaying&&s.Title=="Song"&&s.Artist=="Artist"&&s.Album=="Album","metadata and native clock");
        Check(s.PlatformTrackId=="123","native recording ID retained");
        Check(Math.Abs(s.CurrentPosition-43.123)<.000001&&!s.InterpolateTimeline,"stalled native samples never advance with a timer");
        s=NetEaseBridgeReader.ParseSample(Sample(43.123,200.456,"Pause"),now);Check(!s.IsPlaying&&s.CurrentPosition==43.123,"pause position holds");
        s=NetEaseBridgeReader.ParseSample(Sample(7.1,200.456,"Playing"),now);Check(s.CurrentPosition==7.1,"backward seeks use current sample");
        s=NetEaseBridgeReader.ParseSample(Sample(170.1,200.456,"Playing"),now);Check(s.CurrentPosition==170.1,"forward seeks use current sample");
        s=NetEaseBridgeReader.ParseSample(Sample(200.456,200.456,"End"),now);Check(!s.IsPlaying&&s.CurrentPosition==200.456,"song end holds native end");
        Check(NetEaseBridgeReader.ParseSample(Sample(0,0,"Playing"),now)==null,"missing duration rejected");
        Check(NetEaseBridgeReader.ParseSample(Sample(-1,200,"Playing"),now)==null,"negative progress rejected");
        Check(NetEaseBridgeReader.ParseSample(Sample(210,200,"Playing"),now)==null,"mixed track timeline rejected");
        Check(NetEaseBridgeReader.ParseSample(Sample(double.NaN,200,"Playing"),now)==null,"NaN rejected");
        Check(NetEaseBridgeReader.ParseSample(Sample(20,double.PositiveInfinity,"Playing"),now)==null,"infinity rejected");
        Check(NetEaseBridgeReader.ParseSample(Sample(20,200,"Invalid"),now)==null,"unknown state rejected");
        Check(NetEaseBridgeReader.ParseSample(null,now)==null,"null value rejected");
        var bad=Sample(20,200,"Playing");bad["id"]="";Check(NetEaseBridgeReader.ParseSample(bad,now)==null,"missing track identity rejected");
        foreach(var id in new[]{"0","-1","1&other=2","１２３","999999999999999999999"}) {
            bad=Sample(20,200,"Playing");bad["id"]=id;Check(NetEaseBridgeReader.ParseSample(bad,now)==null,"invalid native ID rejected: "+id);
        }
        bad=Sample(20,200,"Playing");bad["title"]="";Check(NetEaseBridgeReader.ParseSample(bad,now)==null,"missing title rejected");
        Uri uri;Check(NetEaseBridgeReader.TrySocketUri("ws://127.0.0.1:9223/devtools/page/test",out uri),"local socket accepted");
        Check(!NetEaseBridgeReader.TrySocketUri("ws://example.com:9223/devtools/page/test",out uri),"external socket refused");
        Check(!NetEaseBridgeReader.TrySocketUri("ws://127.0.0.1:9224/devtools/page/test",out uri),"other port refused");
        Check(!NetEaseBridgeReader.TrySocketUri("ws://127.0.0.1:9223/devtools/browser/test",out uri),"browser management socket refused");
        Check(NetEaseBridgeReader.IsPlayerPage("orpheus://orpheus/pub/hybrid/index.html"),"NetEase app page accepted");
        Check(NetEaseBridgeReader.IsPlayerPage("orpheus://orpheus/pub/app.html"),"current installed app page accepted");
        Check(!NetEaseBridgeReader.IsPlayerPage("https://music.163.com/"),"web pages refused");
        VerifyProgressClock(now);
        VerifyDirectLyrics().GetAwaiter().GetResult();
        Console.WriteLine("NetEase bridge checks passed: "+passed);
    }

    static MusicSnapshot Snapshot(double position, DateTime at, string state="Playing")
    {return NetEaseBridgeReader.ParseSample(Sample(position,200,state),at);}
    static void VerifyProgressClock(DateTime now)
    {
        var clock=new NetEaseProgressClock();
        var s=clock.Observe(Snapshot(20,now.AddSeconds(-5)));
        Check(!s.InterpolateTimeline,"first sample waits for real advancing progress");
        s=clock.Observe(Snapshot(21,now.AddSeconds(-4)));
        Check(s.InterpolateTimeline&&s.MaximumInterpolationSeconds<=1.2,"normal native pair enables bounded smoothing");
        for(int i=0;i<20;i++)s=clock.Observe(Snapshot(21,now));
        Check(s.TimestampUtc==now.AddSeconds(-4),"duplicate reads preserve original sample time");
        Check(Math.Abs(s.CurrentPosition-22.15)<.001,"stalled native progress stops after one bounded sample");
        s=clock.Observe(Snapshot(21,now,"Pause"));
        Check(!s.InterpolateTimeline&&s.CurrentPosition==21,"pause immediately uses native position");
        s=clock.Observe(Snapshot(21,now));
        Check(!s.InterpolateTimeline,"resume waits for native advancement");
        s=clock.Observe(Snapshot(22,now.AddSeconds(1)));
        Check(s.InterpolateTimeline,"resume smooths only after real advancement");
        s=clock.Observe(Snapshot(7,now.AddSeconds(1.1)));
        Check(!s.InterpolateTimeline&&s.PositionSeconds==7,"backward seek resets prediction");
        s=clock.Observe(Snapshot(150,now.AddSeconds(1.2)));
        Check(!s.InterpolateTimeline&&s.PositionSeconds==150,"forward seek resets prediction");
        var changed=Snapshot(151,now.AddSeconds(2.2));changed.PlatformTrackId="456";
        Check(changed.TrackKey==s.TrackKey&&changed.PlaybackKey!=s.PlaybackKey,"same metadata with different native ID switches playback identity");
        s=clock.Observe(changed);
        Check(!s.InterpolateTimeline,"different recording never inherits another clock");
        clock.Reset();s=clock.Observe(Snapshot(10,now));
        s=clock.Observe(Snapshot(11,now.AddSeconds(4)));
        Check(!s.InterpolateTimeline,"slow or stalled native updates do not establish a clock");
    }
    sealed class LyricsHandler:HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Calls++;token.ThrowIfCancellationRequested();
            Check(request.RequestUri.Host=="music.163.com"&&request.RequestUri.AbsolutePath=="/api/song/lyric","native ID fetch must skip search");
            string lyric=request.RequestUri.Query.Contains("id=456")?"Second recording":"First recording";
            string body=request.RequestUri.Query.Contains("id=789")?"{\"code\":200,\"nolyric\":true}":
                "{\"code\":200,\"lrc\":{\"lyric\":\"[00:00]"+lyric+"\\n[00:02]Tail\"},\"tlyric\":{\"lyric\":\"[00:00]Translated\\n[00:02]Ending\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body)});
        }
    }
    static async Task VerifyDirectLyrics()
    {
        string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"native-lyric-check-"+Guid.NewGuid().ToString("N"));
        try{
            var handler=new LyricsHandler();
            using(var repository=new LyricRepository(folder,handler)) {
                var song=Snapshot(0,DateTime.UtcNow);song.Artist="";song.Album="";
                var document=await repository.FindAsync(song,CancellationToken.None);
                Check(handler.Calls==1&&document.HasTimedLyrics&&document.HasTranslation,"exact native ID returns original and translation in one request even without search metadata");
                document=await repository.FindAsync(song,CancellationToken.None);
                Check(handler.Calls==1&&document.Source.Contains("缓存"),"same native recording reuses ID cache without network");
                song.PlatformTrackId="456";document=await repository.FindAsync(song,CancellationToken.None);
                Check(handler.Calls==2&&document.Lines[0].Text=="Second recording","same-name recordings fetch their own ID and lyrics");
                song.PlatformTrackId="789";document=await repository.FindAsync(song,CancellationToken.None);
                Check(handler.Calls==3&&!document.HasTimedLyrics&&document.Source.Contains("纯音乐"),"native instrumental result never triggers another song search");
                using(var cancel=new CancellationTokenSource()){
                    cancel.Cancel();bool cancelled=false;
                    try{await repository.FindAsync(song,cancel.Token);}catch(OperationCanceledException){cancelled=true;}
                    Check(cancelled&&handler.Calls==3,"cancelled track never issues another request");
                }
            }
        }finally{if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }
}
