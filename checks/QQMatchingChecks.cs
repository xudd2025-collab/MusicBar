using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;
using System.Windows.Forms;
using MusicBar;
internal static class QQMatchingChecks
{
    static int passed;
    static void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;}
    static void DeleteTemporaryDirectory(string directory){
        string root=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(!Path.GetFullPath(directory).StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new Exception("Invalid temporary cleanup path");
        // Antivirus inspection can briefly retain a newly written cache file.
        for(int attempt=0;attempt<10;attempt++){
            try{if(Directory.Exists(directory))Directory.Delete(directory,true);return;}
            catch(IOException){if(attempt==9)throw;Thread.Sleep(100);}
        }
    }
    static MusicSnapshot Track(string title,string album,double duration=261.013){return new MusicSnapshot{Player=MusicPlayer.QQMusic,Title=title,Artist="YOASOBI",Album=album,DurationSeconds=duration};}
    static LyricSearchResult Song(string id,string title,string album,double duration=261.013){return new LyricSearchResult{Player=MusicPlayer.NetEase,Id=id,Title=title,Artist="YOASOBI",Album=album,DurationSeconds=duration};}
    static bool Match(MusicSnapshot observed,params LyricSearchResult[] results){observed.Player=MusicPlayer.NetEase;return LyricRepository.SelectAutomaticMatch(observed,results,true)!=null;}
    [STAThread]
    public static int Main(){try{QrcRecovery().GetAwaiter().GetResult();FastFormats().GetAwaiter().GetResult();Names();ArtistNames();DisplayFormats();BilingualAlbums();BilingualAlbumFallback().GetAwaiter().GetResult();NativeCatalogUpgrade().GetAwaiter().GetResult();EarlyCatalogRecovery().GetAwaiter().GetResult();ProviderDuration();ProviderDurationFallback().GetAwaiter().GetResult();Fallback().GetAwaiter().GetResult();AliasFallback().GetAwaiter().GetResult();CatalogFallback().GetAwaiter().GetResult();ParallelLoading().GetAwaiter().GetResult();ControllerRecovery();NativeControllerSwap();RefreshContinuity();LateInformationRecovery();Console.WriteLine("QQ matching/recovery checks passed: "+passed);return 0;}catch(Exception error){Console.Error.WriteLine(error);return 1;}}
    // Synthetic independent format vector: no song lyrics are distributed.
    const string QrcFixture = "1d3c2506ec0f7d139922dfa218b5ba10d860ad14dceedd6479387ef6fa91204fc54ad42399f0555933949229b04385b65f930c69ae7c6444cd7fb4a360a43a5e0d071cc9a39910b86beebaa827dedea55601385cf8141f29a29a0175b3ccd90b6bac56b856c67b2fbffb5deaa99f3d906482253a34d28cec24abfa0fd0e3d5d865767215b257b0961c173ce41978773982bd8afb97bc3089";
    sealed class QrcHandler : HttpMessageHandler {
        internal int LegacyCalls, WordCalls;
        internal bool Broken, EmptyOrdinary;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
            token.ThrowIfCancellationRequested();string body;
            if(request.RequestUri.Host=="c.y.qq.com") {
                LegacyCalls++;body=new JavaScriptSerializer().Serialize(new{code=0,retcode=0,lyric=Convert.ToBase64String(Encoding.UTF8.GetBytes(EmptyOrdinary?"":"First lineNext line")),trans=""});
            } else if(request.RequestUri.Host=="u.y.qq.com") {
                WordCalls++;string payload=Uri.UnescapeDataString(request.RequestUri.Query);
                Check(payload.Contains("GetPlayLyricInfo")&&payload.Contains("songMID"),"Recovery requests the selected QQ recording's native lyric format");
                body=new JavaScriptSerializer().Serialize(new{code=0,lyrics=new{code=0,data=new{lyric=Broken?"0000000000000000":QrcFixture,trans=""}}});
            } else throw new Exception("Unexpected QRC endpoint");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")});
        }
    }
    static async Task QrcRecovery() {
        string decoded=QQWordLyricDecoder.Decode(QrcFixture);
        Check(decoded.Contains("First(1000,800)"),"Hexadecimal modified-DES zlib QRC format is decoded");
        var words=LrcParser.ParseWordTiming(decoded);
        Check(words.HasTimedLyrics&&words.Lines.Count==2&&words.Lines[0].Text=="First line","Native QRC provides actual lyric rows when ordinary LRC has no timestamps");
        Check(words.Lines[0].Seconds==1&&words.Lines[0].EndSeconds==3&&words.Lines[0].Words.Count==2&&words.Lines[1].Seconds==5,"QRC line and word timestamps are preserved without estimates");
        Check(!LrcParser.ParseWordTiming("First lineNext line").HasTimedLyrics,"Plain text never receives invented timestamps");
        Check(!LrcParser.ParseWordTiming("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///unused'>]><x LyricContent='&e;' />").HasTimedLyrics,"QRC XML cannot load external entities");
        foreach(string bad in new[]{"0","GG00000000000000","0000000000000000","6335a7ee6200586d523234bf9cab558f36b1974196768c2ca27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226a27b02aa779bf226db52bf6036cda8d9",new string('0',2*1024*1024+16)}) {
            bool rejected=false;try{QQWordLyricDecoder.Decode(bad);}catch(InvalidOperationException){rejected=true;}
            Check(rejected,"Malformed or oversized QRC is rejected");
        }
        string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"qrc-check-"+Guid.NewGuid().ToString("N"));
        var song=new LyricSearchResult{Player=MusicPlayer.QQMusic,Id="QrcFixtureA",Title="Fixture A",Artist="Fixture artist",Album="Fixture album",DurationSeconds=10};
        try {
            var handler=new QrcHandler();
            using(var repository=new LyricRepository(directory,handler)) {
                var document=await repository.FetchAsync(song,CancellationToken.None);
                Check(document.HasTimedLyrics&&document.Lines[0].Text=="First line"&&handler.LegacyCalls==1&&handler.WordCalls==1,"Untimed ordinary QQ lyrics automatically recover from native QRC");
            }
            handler=new QrcHandler();
            using(var repository=new LyricRepository(directory,handler)) {
                var document=await repository.FetchAsync(song,CancellationToken.None);
                Check(document.HasTimedLyrics&&document.Lines[0].Words.Count==2&&handler.LegacyCalls==0&&handler.WordCalls==0,"Recovered timeline and words reload from cache without a network request");
            }
            foreach(string file in Directory.GetFiles(Path.Combine(directory,"lyric-cache-v1"),"*.xml")) {
                var xml=new XmlDocument{XmlResolver=null};xml.Load(file);xml.DocumentElement.SelectSingleNode("lrc").InnerText="First lineNext line";xml.DocumentElement.SelectSingleNode("wordTiming").InnerText="";xml.DocumentElement.SelectSingleNode("wordTimingChecked").InnerText="false";xml.Save(file);
            }
            handler=new QrcHandler();
            using(var repository=new LyricRepository(directory,handler)) {
                var document=await repository.FetchAsync(song,CancellationToken.None);
                Check(document.HasTimedLyrics&&handler.LegacyCalls==0&&handler.WordCalls==1,"An old plain-text cache is repaired automatically for any QQ song (legacy="+handler.LegacyCalls+", words="+handler.WordCalls+")");
                song.Id="QrcFixtureB";song.Title="Fixture B";
                document=await repository.FetchAsync(song,CancellationToken.None);
                Check(document.HasTimedLyrics&&handler.LegacyCalls==1&&handler.WordCalls==2,"The next recording independently resolves its native lyric timeline");
            }
            handler=new QrcHandler{EmptyOrdinary=true};song.Id="QrcFixtureEmpty";
            using(var repository=new LyricRepository(directory,handler)) {
                var document=await repository.FetchAsync(song,CancellationToken.None);
                Check(document.HasTimedLyrics&&handler.WordCalls==1,"An empty ordinary lyric response also tries the selected recording's QRC timeline");
            }
            handler=new QrcHandler{Broken=true};song.Id="QrcFixtureBroken";
            using(var repository=new LyricRepository(directory,handler)) {
                bool rejected=false;try{await repository.FetchAsync(song,CancellationToken.None);}catch(InvalidOperationException){rejected=true;}
                Check(rejected,"Unreadable QRC remains a recoverable request failure instead of a successful plain-text cache");
            }
        } finally { DeleteTemporaryDirectory(directory); }
    }
    static MusicSnapshot FancuoTrack(){return new MusicSnapshot{Player=MusicPlayer.QQMusic,Title="犯错",Artist="顾峰 / 斯琴高丽",Album="顾式情歌",DurationSeconds=193.463};}
    sealed class FastFormatHandler : HttpMessageHandler {
        internal readonly TaskCompletionSource<HttpResponseMessage> Ordinary=new TaskCompletionSource<HttpResponseMessage>();
        internal readonly TaskCompletionSource<HttpResponseMessage> Native=new TaskCompletionSource<HttpResponseMessage>();
        internal readonly TaskCompletionSource<bool> BothStarted=new TaskCompletionSource<bool>();
        internal int OrdinaryCalls, NativeCalls;
        internal bool OrdinaryCancelled, NativeCancelled;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
            bool ordinary=request.RequestUri.Host=="c.y.qq.com";
            var pending=ordinary?Ordinary:Native;
            if(ordinary)OrdinaryCalls++;else NativeCalls++;
            var registration=token.Register(delegate{if(ordinary)OrdinaryCancelled=true;else NativeCancelled=true;pending.TrySetCanceled();});
            pending.Task.ContinueWith(delegate{registration.Dispose();},TaskScheduler.Default);
            if(OrdinaryCalls>0&&NativeCalls>0)BothStarted.TrySetResult(true);
            return pending.Task;
        }
        internal void ReplyOrdinary(bool timed=true,bool failure=false) {
            string text=timed?"[00:01]First line\n[00:05]Next":"First lineNext";
            Ordinary.TrySetResult(LoadingHandler.Reply(new JavaScriptSerializer().Serialize(new{code=failure?1:0,lyric=Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),trans=""})));
        }
        internal void ReplyNative(bool translation=false,bool failure=false) {
            Native.TrySetResult(LoadingHandler.Reply(new JavaScriptSerializer().Serialize(new{code=0,lyrics=new{code=failure?1:0,data=new{lyric=QrcFixture,trans=translation?Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01]第一行\n[00:05]下一句")):""}}})));
        }
    }
    static async Task Ready(Task task,string message) {
        Check(await Task.WhenAny(task,Task.Delay(2000)).ConfigureAwait(false)==task,message);
    }
    static async Task FastFormats() {
        string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"fast-format-"+Guid.NewGuid().ToString("N"));
        var song=new LyricSearchResult{Player=MusicPlayer.QQMusic,Id="FastNative",Title="Fixture",Artist="Fixture artist",Album="Fixture album",DurationSeconds=10};
        try {
            var handler=new FastFormatHandler();
            using(var repository=new LyricRepository(directory,handler,true)) {
                var pending=repository.FetchAsync(song,CancellationToken.None);
                await Ready(handler.BothStarted.Task,"Both lyric formats start without waiting for an ordinary response");
                handler.ReplyNative();await Ready(pending,"Native timeline returns while ordinary endpoint is blocked");
                var document=await pending;
                Check(document.HasTimedLyrics&&document.Lines[0].Words.Count==2&&handler.OrdinaryCancelled,"Native lyrics keep true word timing and cancel the slower ordinary request");
                document=await repository.FetchAsync(song,CancellationToken.None);
                Check(document.HasTimedLyrics&&handler.NativeCalls==1&&handler.OrdinaryCalls==1,"Completed native response without translation is cached without duplicate requests");
            }
            handler=new FastFormatHandler();song.Id="FastOrdinary";
            using(var repository=new LyricRepository(directory,handler,true)) {
                var pending=repository.FetchAsync(song,CancellationToken.None);
                await Ready(handler.BothStarted.Task,"Ordinary and native requests share the same selected song");
                handler.ReplyOrdinary();await Ready(pending,"Ordinary lyrics display while native translation is blocked");
                var document=await pending;
                Check(document.HasTimedLyrics&&!handler.NativeCancelled&&handler.NativeCalls==1,"Pending native response is reused for enrichment");
                handler.ReplyNative(true);
                var deadline=DateTime.UtcNow.AddSeconds(2);
                while(!document.WordTimingChecked&&DateTime.UtcNow<deadline)await Task.Delay(10);
                Check(document.WordTimingChecked&&document.HasTranslation&&document.Lines[0].Words.Count==2&&handler.NativeCalls==1,"Reused response supplies words and translation with one native request");
                document=await repository.FetchAsync(song,CancellationToken.None);
                Check(document.HasTranslation&&document.HasTimedLyrics&&handler.NativeCalls==1,"Enriched bilingual cache is reused without extra requests");
            }
            handler=new FastFormatHandler();song.Id="FastCancelled";
            using(var repository=new LyricRepository(directory,handler,true))
            using(var cancellation=new CancellationTokenSource()) {
                var pending=repository.FetchAsync(song,cancellation.Token);
                await Ready(handler.BothStarted.Task,"Cancellation fixture starts both formats");
                cancellation.Cancel();await Ready(pending,"Track cancellation ends a lookup while both endpoints are blocked");
                bool cancelled=false;try{await pending;}catch(OperationCanceledException){cancelled=true;}
                Check(cancelled&&handler.OrdinaryCancelled&&handler.NativeCancelled,"Track changes cancel both formats without publishing stale lyrics");
            }
            handler=new FastFormatHandler();song.Id="FastOrdinaryFailure";
            using(var repository=new LyricRepository(directory,handler,true)) {
                var pending=repository.FetchAsync(song,CancellationToken.None);
                await Ready(handler.BothStarted.Task,"Failure fixture starts both formats");
                handler.ReplyOrdinary(false,true);handler.ReplyNative();await Ready(pending,"An ordinary API failure does not block valid native lyrics");
                Check((await pending).HasTimedLyrics,"Valid native lyrics survive an ordinary API failure");
            }
            handler=new FastFormatHandler();song.Id="FastNativeFailure";
            using(var repository=new LyricRepository(directory,handler,true)) {
                var pending=repository.FetchAsync(song,CancellationToken.None);
                await Ready(handler.BothStarted.Task,"Native failure fixture starts both formats");
                handler.ReplyNative(false,true);handler.ReplyOrdinary();await Ready(pending,"Native API failure does not block valid ordinary lyrics");
                Check((await pending).HasTimedLyrics,"Valid ordinary lyrics survive a native API failure");
            }
        } finally { DeleteTemporaryDirectory(directory); }
    }
    static LyricSearchResult FancuoSong(string id="89551"){return new LyricSearchResult{Player=MusicPlayer.NetEase,Id=id,Title="犯错",Artist="顾峰 / 斯琴高丽",Album="顾式情歌",DurationSeconds=196.320};}
    static void ProviderDuration(){
        Check(Match(FancuoTrack(),FancuoSong()),"QQ alternative accepts the actual 193.463/196.320 second recording with exact complete identity");
        var observed=FancuoTrack();observed.Player=MusicPlayer.NetEase;
        Check(LyricRepository.SelectAutomaticMatch(observed,new[]{FancuoSong()})==null,"Native NetEase retains its normal duration tolerance");
        observed=FancuoTrack();var candidate=FancuoSong();candidate.Player=MusicPlayer.QQMusic;
        Check(LyricRepository.SelectAutomaticMatch(observed,new[]{candidate})==null,"Primary QQ matching retains its normal duration tolerance");
        foreach(int mismatch in new[]{0,1,2,3,4,5,6,7,8}){
            candidate=FancuoSong();
            if(mismatch==0)candidate.Album="其他专辑";
            if(mismatch==1)candidate.Artist="顾峰";
            if(mismatch==2)candidate.Artist+=" / 其他歌手";
            if(mismatch==3)candidate.Title+=" (Live)";
            if(mismatch==4)candidate.DurationSeconds=197.464;
            if(mismatch==5)candidate.DurationSeconds=double.NaN;
            if(mismatch==6)candidate.DurationSeconds=double.PositiveInfinity;
            if(mismatch==7)candidate.DurationSeconds=0;
            if(mismatch==8){candidate.Album="Other album";candidate.AlbumAliases.Add("顾式情歌");}
            Check(!Match(FancuoTrack(),candidate),"Expanded provider tolerance rejects incompatible identity or duration: "+mismatch);
        }
        foreach(int missing in new[]{0,1}){
            observed=FancuoTrack();if(missing==0)observed.Artist="";else observed.Album="";
            Check(!Match(observed,FancuoSong()),"Expanded provider tolerance requires complete observed identity: "+missing);
        }
        observed=FancuoTrack();observed.DurationSeconds=double.PositiveInfinity;
        Check(!Match(observed,FancuoSong()),"Nonfinite observed duration cannot use provider tolerance");
        candidate=FancuoSong();candidate.DurationSeconds=197.463;
        Check(Match(FancuoTrack(),candidate),"Four seconds is the maximum exact provider duration allowance");
        candidate=FancuoSong();candidate.Artist="斯琴高丽 / 顾峰";
        Check(Match(FancuoTrack(),candidate),"The full artist set can be listed in a different order");
        candidate=FancuoSong("duplicate");candidate.DurationSeconds=196;
        Check(!Match(FancuoTrack(),FancuoSong(),candidate),"Two provider recordings remain ambiguous");
        candidate.DurationSeconds=193.463;
        Check(!Match(FancuoTrack(),FancuoSong(),candidate),"A nearest duration cannot resolve expanded provider ambiguity");
        observed=FancuoTrack();observed.Title="犯错 (歌曲译名)";
        Check(!Match(observed,FancuoSong()),"Display title compensation cannot combine with expanded duration");
        observed=FancuoTrack();observed.Artist="顾峰 (Gu Feng) / 斯琴高丽";
        Check(!Match(observed,FancuoSong()),"Artist alias compensation cannot combine with expanded duration");
    }
    sealed class ProviderDurationHandler:HttpMessageHandler{
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
            token.ThrowIfCancellationRequested();Calls++;string body;
            if(request.RequestUri.Host=="u.y.qq.com")body="{\"code\":0,\"request\":{\"code\":2001}}";
            else if(request.RequestUri.AbsolutePath=="/api/search/get/web")
                body=new JavaScriptSerializer().Serialize(new{code=200,result=new{songs=new[]{new{id=89551,name="犯错",duration=196320,artists=new[]{new{name="顾峰"},new{name="斯琴高丽"}},album=new{name="顾式情歌"}}}}});
            else if(request.RequestUri.AbsolutePath=="/api/song/lyric")
                body=new JavaScriptSerializer().Serialize(new{code=200,lrc=new{lyric="[00:01]沉默不是代表我的错\n[00:04]下一句"}});
            else throw new Exception("Unexpected provider-duration fixture endpoint");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")});
        }
    }
    static async Task ProviderDurationFallback(){
        string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"qq-provider-duration-"+Guid.NewGuid().ToString("N"));
        try{
            var handler=new ProviderDurationHandler();
            using(var repository=new LyricRepository(directory,handler)){
                var document=await repository.FindAsync(FancuoTrack(),CancellationToken.None);
                Check(document!=null&&document.HasTimedLyrics&&document.Source.Contains("QQ 音乐备用歌词")&&handler.Calls==3,"QQ code 2001 falls back automatically for the actual 犯错 recording");
                Check(Directory.Exists(Path.Combine(directory,"lyric-match-v1"))&&Directory.GetFiles(Path.Combine(directory,"lyric-match-v1")).Length==1,"Provider duration match persists its recording binding");
                Check(Directory.Exists(Path.Combine(directory,"lyric-cache-v1"))&&Directory.GetFiles(Path.Combine(directory,"lyric-cache-v1")).Length==1,"Provider duration match persists its timed lyric cache");
            }
            handler=new ProviderDurationHandler();
            using(var repository=new LyricRepository(directory,handler)){
                var document=await repository.FindAsync(FancuoTrack(),CancellationToken.None);
                Check(document!=null&&document.HasTimedLyrics&&document.Source.Contains("缓存")&&handler.Calls==0,"Reopening validates the expanded provider binding and uses cached lyrics without network (source="+(document==null?"null":document.Source)+", calls="+handler.Calls+")");
                var changed=FancuoTrack();changed.DurationSeconds=180;
                bool rejected=false;try{await repository.FindAsync(changed,CancellationToken.None);}catch(InvalidOperationException error){rejected=error.Message.Contains("2001");}
                Check(rejected&&handler.Calls==2,"Changed recording duration cannot reuse the fallback cache binding");
            }
        }finally{DeleteTemporaryDirectory(directory);}
    }
    static void Names(){
        Check(LyricRepository.TrimTranslatedDisplaySuffix("鳥の詩（鸟之诗）")=="鳥の詩","Japanese title with full-width Chinese translation");
        Check(LyricRepository.TrimTranslatedDisplaySuffix("扉をあけて (打开门)")=="扉をあけて","ANZA display translation");
        Check(LyricRepository.TrimTranslatedDisplaySuffix("Departures ~あなたにおくるアイの歌~ (离别 ~赠予你的爱之歌~)")=="Departures ~あなたにおくるアイの歌~","EGOIST translated subtitle preserves original version words");
        foreach(string title in new[]{"夜に駆ける (Live)","夜に駆ける（现场版）","夜に駆ける（翻唱）","夜に駆ける（伴奏）","夜に駆ける（短版）","Song (Explicit)","Song (中文译名)","夜に駆ける（TV动画片尾曲）"})
            Check(LyricRepository.TrimTranslatedDisplaySuffix(title)==title.Normalize(NormalizationForm.FormKC),"Recording label stays significant: "+title);
        var correct=Song("1409311773","夜に駆ける","夜に駆ける");
        Check(Match(Track("夜に駆ける (向夜晚奔去)","夜に駆ける (向夜晚奔去)"),correct),"Full recording identity permits QQ display translation");
        Check(!Match(Track("夜に駆ける (向夜晚奔去)","THE BOOK"),correct),"Wrong album cannot use display-name compensation");
        Check(!Match(Track("夜に駆ける (向夜晚奔去)","夜に駆ける",230),correct),"Wrong recording duration rejected");
        Check(!Match(Track("夜に駆ける (向夜晚奔去)","夜に駆ける",0),correct),"No duration means no translated display match");
        Check(!Match(Track("夜に駆ける (向夜晚奔去)",""),correct),"No album means no translated display match");
        var noArtist=Track("夜に駆ける (向夜晚奔去)","夜に駆ける");noArtist.Artist="";
        Check(!Match(noArtist,correct),"No artist means no translated display match");
        var cover=Song("2","夜に駆ける","夜に駆ける");cover.Artist="Cover singer";
        Check(!Match(Track("夜に駆ける (向夜晚奔去)","夜に駆ける"),cover),"A cover never satisfies the original artist");
        Check(!Match(Track("夜に駆ける (向夜晚奔去)","夜に駆ける"),Song("3","夜に駆ける (Live)","夜に駆ける")),"Candidate live version never loses its label");
        Check(!Match(Track("夜に駆ける（现场版）","夜に駆ける"),correct),"Observed live version cannot fall back to studio");
        Check(!Match(Track("夜に駆ける (向夜晚奔去)","夜に駆ける"),correct,Song("4","夜に駆ける","夜に駆ける")),"Two matching recordings stay ambiguous");
        var ordinaryNetEase=Track("夜に駆ける (向夜晚奔去)","夜に駆ける");ordinaryNetEase.Player=MusicPlayer.NetEase;
        Check(LyricRepository.SelectAutomaticMatch(ordinaryNetEase,new[]{correct})==null,"QQ display convention is not applied to native NetEase metadata");
        Check(Match(Track("Departures ~あなたにおくるアイの歌~ (离别)","Departures~あなたにおくるアイの歌~"),Song("5","Departures〜あなたにおくるアイの歌〜","Departures~あなたにおくるアイの歌~")),"Japanese wave-dash shapes and surrounding spaces are cosmetic");
    }
    static MusicSnapshot AkumaTrack(){return new MusicSnapshot{Player=MusicPlayer.QQMusic,Title="悪魔の子 (恶魔之子)",Artist="ヒグチアイ (HiguchiAi)",Album="悪魔の子",DurationSeconds=227.679};}
    static LyricSearchResult AkumaSong(string id="1910623420"){return new LyricSearchResult{Player=MusicPlayer.NetEase,Id=id,Title="悪魔の子",Artist="ヒグチアイ",Album="悪魔の子",DurationSeconds=227.679};}
    static void ArtistNames(){
        foreach(var pair in new[]{new[]{"ヒグチアイ (HiguchiAi)","ヒグチアイ"},new[]{"ヒグチアイ（HiguchiAi）","ヒグチアイ"},new[]{"Lia (りあ)","Lia"},new[]{"Aimer (エメ)","Aimer"},new[]{"双笙 (陈元汐)","双笙"},new[]{"J.Fla (Kim Jung Hwa)","J.Fla"},new[]{"Cem Adrian (Cem Filiz)","Cem Adrian"}})
            Check(LyricRepository.TrimQQArtistDisplayNames(pair[0])==pair[1],"QQ artist display alias: "+pair[0]);
        foreach(string name in new[]{"ヒグチアイ (Live)","ヒグチアイ (DJ)","ヒグチアイ (Cover)","ヒグチアイ (feat. Lia)","ヒグチアイ (Guest)","ヒグチアイ（现场版）","ヒグチアイ (A / B)","Aimer (Project & X)","Artist (Band)"})
            Check(LyricRepository.TrimQQArtistDisplayNames(name)==name.Normalize(NormalizationForm.FormKC),"Performance and collaboration labels remain significant: "+name);
        Check(Match(AkumaTrack(),AkumaSong()),"QQ artist alias accepts the same title, album, duration and canonical artist");
        var wrong=AkumaSong();wrong.Artist="Cover singer";Check(!Match(AkumaTrack(),wrong),"Alias matching rejects a different performer");
        wrong=AkumaSong();wrong.Artist="HiguchiAi";Check(!Match(AkumaTrack(),wrong),"Alias handling does not guess transliterations between canonical names");
        wrong=AkumaSong();wrong.Album="最悪最愛";Check(!Match(AkumaTrack(),wrong),"Alias matching requires the exact album");
        wrong=AkumaSong();wrong.DurationSeconds=230.5;Check(!Match(AkumaTrack(),wrong),"Alias matching rejects a different duration");
        var observed=AkumaTrack();observed.Album="";Check(!Match(observed,AkumaSong()),"Alias compensation is disabled without album identity");
        observed=AkumaTrack();observed.DurationSeconds=0;Check(!Match(observed,AkumaSong()),"Alias compensation is disabled without measured duration");
        wrong=AkumaSong();wrong.Artist="ヒグチアイ / Another artist";Check(!Match(AkumaTrack(),wrong),"Artist aliases never remove a co-performer");
        observed=AkumaTrack();observed.Artist+=" / Another artist";Check(!Match(observed,AkumaSong()),"Observed duet cannot match a solo performer");
        wrong=AkumaSong("1925436021");wrong.DurationSeconds=227.6;
        Check(!Match(AkumaTrack(),AkumaSong(),wrong),"Several alias candidates stay ambiguous even when durations differ slightly");
        observed=AkumaTrack();observed.Player=MusicPlayer.NetEase;
        Check(LyricRepository.SelectAutomaticMatch(observed,new[]{AkumaSong()})==null,"QQ artist display conventions do not change native NetEase identity");
    }
    static MusicSnapshot RefrainTrack(){return new MusicSnapshot{Player=MusicPlayer.QQMusic,Title="Ref:rain",Artist="Aimer (エメ)",Album="ペニーレイン (Penny Rain)",DurationSeconds=289.986};}
    static LyricSearchResult RefrainSong(string id="1357775677"){return new LyricSearchResult{Player=MusicPlayer.NetEase,Id=id,Title="Ref:rain",Artist="Aimer",Album="Penny Rain",DurationSeconds=289.986};}
    static void BilingualAlbums(){
        foreach(string album in new[]{"ペニーレイン (Penny Rain)","ペニーレイン（Penny Rain）","Penny Rain (ペニーレイン)"}){
            var observed=RefrainTrack();observed.Album=album;
            var correct=RefrainSong();var single=RefrainSong("536623501");single.Album="Ref:rain / 眩いばかり";
            Check(LyricRepository.SelectAutomaticMatch(new MusicSnapshot{Player=MusicPlayer.NetEase,Title=observed.Title,Artist=observed.Artist,Album=album,DurationSeconds=observed.DurationSeconds},new[]{single,correct},true)==correct,"QQ bilingual album selects the album recording rather than the same-title single: "+album);
        }
        var wrong=RefrainSong();wrong.Album="Other Album";Check(!Match(RefrainTrack(),wrong),"Bilingual album cannot match a different album");
        wrong=RefrainSong();wrong.Artist="Cover artist";Check(!Match(RefrainTrack(),wrong),"Bilingual album cannot match another performer");
        wrong=RefrainSong();wrong.DurationSeconds=299;Check(!Match(RefrainTrack(),wrong),"Bilingual album cannot match the wrong duration");
        wrong=RefrainSong();wrong.Title="Ref:rain -3 nuits ver.-";Check(!Match(RefrainTrack(),wrong),"Bilingual album preserves recording-version words");
        Check(!Match(RefrainTrack(),RefrainSong(),RefrainSong("2")),"Several same-album recordings remain ambiguous");
        var incomplete=RefrainTrack();incomplete.DurationSeconds=0;Check(!Match(incomplete,RefrainSong()),"Bilingual display compensation requires measured duration");
        incomplete=RefrainTrack();incomplete.Artist="";Check(!Match(incomplete,RefrainSong()),"Bilingual display compensation requires performer identity");
        var native=RefrainTrack();native.Player=MusicPlayer.NetEase;Check(LyricRepository.SelectAutomaticMatch(native,new[]{RefrainSong()})==null,"Native NetEase matching does not inherit QQ bilingual display conventions");
        foreach(string label in new[]{"Live","TV size","Deluxe","Limited Edition","ライブ","リミックス","初回限定盤"}){
            var observed=RefrainTrack();observed.Artist="Aimer";observed.Album="ペニーレイン ("+label+")";
            var candidate=RefrainSong();candidate.Album="ペニーレイン";
            Check(!Match(observed,candidate),"Album edition qualifier is never stripped: "+label);
            observed=RefrainTrack();observed.Album="ペニーレイン (Penny Rain "+label+")";
            candidate=RefrainSong();candidate.Album="Penny Rain "+label;
            Check(!Match(observed,candidate),"A bracketed edition label is not accepted as a translation: "+label);
        }
    }
    sealed class BilingualAlbumHandler:HttpMessageHandler{
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
            token.ThrowIfCancellationRequested();Calls++;string body;
            if(request.RequestUri.Host=="u.y.qq.com")body="{\"code\":0,\"request\":{\"code\":2001}}";
            else if(request.RequestUri.AbsolutePath=="/api/search/get/web"){
                Check(Uri.UnescapeDataString(request.RequestUri.Query).Contains("Ref:rain Aimer"),"Bilingual lookup searches the undecorated canonical artist");
                body=new JavaScriptSerializer().Serialize(new{code=200,result=new{songs=new[]{new{id=536623501,name="Ref:rain",duration=290202,artists=new[]{new{name="Aimer"}},album=new{name="Ref:rain / 眩いばかり"}},new{id=1357775677,name="Ref:rain",duration=289986,artists=new[]{new{name="Aimer"}},album=new{name="Penny Rain"}}}}});
            }else if(request.RequestUri.AbsolutePath=="/api/song/lyric"){
                Check(request.RequestUri.Query.Contains("id=1357775677"),"Bilingual fallback fetches the exact album recording ID");
                body="{\"code\":200,\"lrc\":{\"lyric\":\"[00:01]Original line\\n[00:03]Next line\"},\"tlyric\":{\"lyric\":\"[00:01]测试译文\\n[00:03]下一句\"}}";
            }else throw new Exception("Unexpected bilingual album endpoint");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")});
        }
    }
    static async Task BilingualAlbumFallback(){
        string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"qq-bilingual-album-"+Guid.NewGuid().ToString("N"));
        try{
            var handler=new BilingualAlbumHandler();
            using(var repository=new LyricRepository(directory,handler)){
                var document=await repository.FindAsync(RefrainTrack(),CancellationToken.None);
                Check(document!=null&&document.HasTimedLyrics&&document.HasTranslation&&handler.Calls==3,"QQ 2001 recovers timed original and translation for bilingual album metadata");
            }
            handler=new BilingualAlbumHandler();
            using(var repository=new LyricRepository(directory,handler)){
                var document=await repository.FindAsync(RefrainTrack(),CancellationToken.None);
                Check(document!=null&&document.HasTimedLyrics&&document.Source.Contains("缓存")&&handler.Calls==0,"Bilingual recording identity survives restart and reuses cache without search");
            }
        }finally{DeleteTemporaryDirectory(directory);}
    }
    sealed class NativeCatalogHandler:HttpMessageHandler{
        internal int Calls, NativeCalls;
        internal bool Ambiguous, WrongAlbum, SlowSuggestions;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
            token.ThrowIfCancellationRequested();Interlocked.Increment(ref Calls);string body;
            string query=Uri.UnescapeDataString(request.RequestUri.Query);
            if(request.RequestUri.AbsolutePath=="/splcloud/fcgi-bin/smartbox_new.fcg"){
                if(SlowSuggestions)await Task.Delay(200,token).ConfigureAwait(false);
                body="{\"code\":0,\"data\":{\"song\":{\"itemlist\":[{\"mid\":\"hintfixture\",\"name\":\"Ref:rain\",\"singer\":\"Aimer\"},{\"mid\":\"coverfixture\",\"name\":\"Ref:rain\",\"singer\":\"Cover artist\"}]}}}";
            }else if(request.RequestUri.Host=="u.y.qq.com"&&query.Contains("get_song_detail_yqq")){
                Check(query.Contains("hintfixture")&&!query.Contains("coverfixture"),"Native catalog ignores another performer's suggestion");
                body="{\"code\":0,\"catalog\":{\"code\":0,\"data\":{\"track_info\":{\"mid\":\"hintfixture\",\"title\":\"Ref:rain\",\"interval\":290,\"album\":{\"name\":\"Single album\"},\"singer\":[{\"name\":\"Aimer\",\"mid\":\"artistfixture\"}]}}}}";
            }else if(request.RequestUri.Host=="u.y.qq.com"&&query.Contains("GetAlbumList")){
                body=new JavaScriptSerializer().Serialize(new{code=0,catalog=new{code=0,data=new{albumList=new[]{new{albumMid="albumfixture",albumName=WrongAlbum?"Other album":"ペニーレイン (Penny Rain)",albumTranName=WrongAlbum?"Other album":"Penny Rain"}}}}});
            }else if(request.RequestUri.Host=="u.y.qq.com"&&query.Contains("GetAlbumSongList")){
                var rows=new List<object>();
                rows.Add(new{songInfo=new{mid="nativefixture",title="Ref:rain",interval=289,album=new{name="ペニーレイン",title="ペニーレイン (Penny Rain)"},singer=new[]{new{name="Aimer"}}}});
                if(Ambiguous)rows.Add(new{songInfo=new{mid="secondfixture",title="Ref:rain",interval=289,album=new{title="ペニーレイン (Penny Rain)"},singer=new[]{new{name="Aimer"}}}});
                rows.Add(new{songInfo=new{mid="wrongversion",title="Ref:rain (Live)",interval=289,album=new{title="ペニーレイン (Penny Rain)"},singer=new[]{new{name="Aimer"}}}});
                body=new JavaScriptSerializer().Serialize(new{code=0,catalog=new{code=0,data=new{songList=rows}}});
            }else if(request.RequestUri.Host=="u.y.qq.com"&&query.Contains("GetPlayLyricInfo")){
                Interlocked.Increment(ref NativeCalls);Check(query.Contains("nativefixture"),"Native upgrade requests the verified album's own lyric ID");
                body=new JavaScriptSerializer().Serialize(new{code=0,lyrics=new{code=0,data=new{lyric=QrcFixture,trans=Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01]平台同源译文\n[00:05]下一句"))}}});
            }else if(request.RequestUri.Host=="u.y.qq.com")body="{\"code\":0,\"request\":{\"code\":2001}}";
            else if(request.RequestUri.AbsolutePath=="/api/search/get/web")body="{\"code\":200,\"result\":{\"songs\":[{\"id\":1357775677,\"name\":\"Ref:rain\",\"duration\":289986,\"artists\":[{\"name\":\"Aimer\"}],\"album\":{\"name\":\"Penny Rain\"}}]}}";
            else if(request.RequestUri.AbsolutePath=="/api/song/lyric")body="{\"code\":200,\"lrc\":{\"lyric\":\"[00:01]Alternate line\\n[00:05]Next alternate\"},\"tlyric\":{\"lyric\":\"[00:01]备用译文\\n[00:05]备用下一句\"}}";
            else throw new Exception("Unexpected native catalog endpoint");
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")};
        }
    }
    static async Task NativeCatalogUpgrade(){
        string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"qq-native-upgrade-"+Guid.NewGuid().ToString("N"));
        try{
            var handler=new NativeCatalogHandler();var data=Path.Combine(directory,"success");
            using(var repository=new LyricRepository(data,handler)){
                var previous=await repository.FindAsync(RefrainTrack(),CancellationToken.None).ConfigureAwait(false);
                Check(previous!=null&&previous.Source.Contains("备用歌词")&&previous.NativeCompletion==null,"An existing alternate cache is preserved before native migration");
            }
            handler=new NativeCatalogHandler();
            using(var repository=new LyricRepository(data,handler,true)){
                var initial=await repository.FindAsync(RefrainTrack(),CancellationToken.None).ConfigureAwait(false);
                Check(initial!=null&&initial.HasTimedLyrics&&initial.Source.Contains("备用歌词"),"Verified alternate lyrics remain available while the native catalog is checked");
                Check(initial.NativeCompletion!=null,"Alternate lyric has one native recording completion task");
                await initial.NativeCompletion.ConfigureAwait(false);
                var native=initial.NativeReplacement;
                Check(native!=null&&native.Source=="QQ 音乐"&&native.Lines[0].Text=="First line"&&native.TranslationLines[0].Text=="平台同源译文"&&native.Lines[0].Words.Count==2,"Native upgrade publishes original, translation and word timestamps from one QQ recording");
            }
            handler=new NativeCatalogHandler();
            using(var repository=new LyricRepository(data,handler,true)){
                var cached=await repository.FindAsync(RefrainTrack(),CancellationToken.None).ConfigureAwait(false);
                Check(cached!=null&&cached.Source.StartsWith("QQ 音乐")&&cached.Source.Contains("缓存")&&cached.Lines[0].Words.Count==2&&handler.Calls==0,"Native binding and QRC-only cache survive restart without network or alternate enrichment");
            }
            foreach(bool wrongAlbum in new[]{false,true}){
                handler=new NativeCatalogHandler{Ambiguous=!wrongAlbum,WrongAlbum=wrongAlbum};
                using(var repository=new LyricRepository(Path.Combine(directory,wrongAlbum?"wrong-album":"ambiguous"),handler,true)){
                    var initial=await repository.FindAsync(RefrainTrack(),CancellationToken.None).ConfigureAwait(false);
                    await initial.NativeCompletion.ConfigureAwait(false);
                    Check(initial.NativeReplacement==null&&handler.NativeCalls==0,"Native completion rejects "+(wrongAlbum?"another album":"ambiguous same-album recordings"));
                }
            }
            handler=new NativeCatalogHandler{SlowSuggestions=true};
            using(var cancellation=new CancellationTokenSource())
            using(var repository=new LyricRepository(Path.Combine(directory,"cancelled"),handler,true)){
                var initial=await repository.FindAsync(RefrainTrack(),cancellation.Token).ConfigureAwait(false);
                cancellation.Cancel();await initial.NativeCompletion.ConfigureAwait(false);
                Check(initial.NativeReplacement==null&&handler.NativeCalls==0,"Cancelling a song lookup prevents its pending native enrichment from publishing");
            }
        }finally{DeleteTemporaryDirectory(directory);}
    }
    static MusicSnapshot AvidTrack(){return new MusicSnapshot{Player=MusicPlayer.QQMusic,Title="Avid",Artist="SawanoHiroyuki[nZk]",Album="Avid / Hands Up to the Sky",DurationSeconds=250.293};}
    sealed class EarlyCatalogHandler:HttpMessageHandler{
        internal int SearchCalls, CatalogCalls, DetailCalls, AlbumCalls, LyricCalls;
        internal bool Restricted, WrongAlbum, WrongDuration, Ambiguous, SlowCatalog, FastPrimary;
        internal bool PrimaryCancelled, AlternateCancelled;
        internal readonly TaskCompletionSource<bool> CatalogStarted=new TaskCompletionSource<bool>();
        internal readonly TaskCompletionSource<bool> BothSearchesStarted=new TaskCompletionSource<bool>();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
            token.ThrowIfCancellationRequested();string body;string query=Uri.UnescapeDataString(request.RequestUri.Query);
            if(request.RequestUri.AbsolutePath=="/splcloud/fcgi-bin/smartbox_new.fcg"){
                Interlocked.Increment(ref CatalogCalls);CatalogStarted.TrySetResult(true);
                if(SlowCatalog)await Task.Delay(Timeout.Infinite,token).ConfigureAwait(false);
                var hints=new List<object>{new{mid="avidoriginal",name="Avid",singer="SawanoHiroyuki[nZk]/瑞葵"},new{mid="otherartist",name="Avid",singer="Other artist"}};
                if(Ambiguous)hints.Add(new{mid="avidsecond",name="Avid",singer="SawanoHiroyuki[nZk]/瑞葵"});
                body=new JavaScriptSerializer().Serialize(new{code=0,data=new{song=new{itemlist=hints}}});
            }else if(request.RequestUri.Host=="u.y.qq.com"&&query.Contains("get_song_detail_yqq")){
                Interlocked.Increment(ref DetailCalls);Check(!query.Contains("otherartist"),"Early native catalog never follows another performer's hint");
                var track=new{mid=query.Contains("avidsecond")?"avidsecond":"avidoriginal",title="Avid",interval=WrongDuration?210:250,album=new{title=WrongAlbum?"Different album":"Avid / Hands Up to the Sky"},singer=new[]{new{name="SawanoHiroyuki[nZk]",mid="primaryartist"},new{name="瑞葵",mid="guestartist"}}};
                body=new JavaScriptSerializer().Serialize(new{code=0,catalog=new{code=0,data=new{track_info=track}}});
            }else if(request.RequestUri.Host=="u.y.qq.com"&&query.Contains("GetAlbumList")){
                Interlocked.Increment(ref AlbumCalls);body="{\"code\":0,\"catalog\":{\"code\":0,\"data\":{\"albumList\":[]}}}";
            }else if(request.RequestUri.Host=="c.y.qq.com"&&request.RequestUri.AbsolutePath.Contains("fcg_query_lyric_new")){
                Interlocked.Increment(ref LyricCalls);Check(query.Contains("avidoriginal"),"Early native catalog fetches only the full identity's lyric ID");
                if(!FastPrimary)await Task.Delay(Timeout.Infinite,token).ConfigureAwait(false);
                body=new JavaScriptSerializer().Serialize(new{code=0,lyric=Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01]First line\n[00:05]Next")),trans=Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01]原平台译文\n[00:05]下一句"))});
            }else if(request.RequestUri.Host=="u.y.qq.com"&&query.Contains("GetPlayLyricInfo")){
                body=new JavaScriptSerializer().Serialize(new{code=0,lyrics=new{code=0,data=new{lyric=QrcFixture,trans=Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01]原平台译文\n[00:05]下一句"))}}});
            }else{
                bool primary=request.RequestUri.Host=="u.y.qq.com";
                if(Interlocked.Increment(ref SearchCalls)>=2)BothSearchesStarted.TrySetResult(true);
                if(FastPrimary&&primary){
                    body="{\"code\":0,\"request\":{\"code\":0,\"data\":{\"body\":{\"song\":{\"list\":[{\"mid\":\"avidoriginal\",\"title\":\"Avid\",\"interval\":250,\"album\":{\"title\":\"Avid / Hands Up to the Sky\"},\"singer\":[{\"name\":\"SawanoHiroyuki[nZk]\"},{\"name\":\"瑞葵\"}]}]}}}}}";
                }else if(Restricted){body=primary?"{\"code\":0,\"request\":{\"code\":2001}}":"{\"code\":200,\"result\":{\"songs\":[]}}";}
                else{
                    try{await Task.Delay(Timeout.Infinite,token).ConfigureAwait(false);}
                    catch(OperationCanceledException){if(primary)PrimaryCancelled=true;else AlternateCancelled=true;throw;}
                    throw new Exception("Blocked search returned without cancellation");
                }
            }
            return LoadingHandler.Reply(body);
        }
    }
    static async Task EarlyCatalogRecovery(){
        string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"qq-early-native-"+Guid.NewGuid().ToString("N"));
        try{
            foreach(bool restricted in new[]{false,true}){
                var handler=new EarlyCatalogHandler{Restricted=restricted};var data=Path.Combine(directory,restricted?"restricted":"slow");
                using(var repository=new LyricRepository(data,handler,true)){
                    var watch=Stopwatch.StartNew();var document=await repository.FindAsync(AvidTrack(),CancellationToken.None).ConfigureAwait(false);
                    Check(document!=null&&document.HasTimedLyrics&&document.HasTranslation&&document.Source.StartsWith("QQ 音乐")&&!document.Source.Contains("备用歌词")&&watch.Elapsed.TotalSeconds<2,"Native catalog displays same-recording bilingual lyrics promptly when normal searches are "+(restricted?"restricted/unmatched":"blocked"));
                    Check(handler.CatalogCalls==1&&handler.DetailCalls==1&&handler.AlbumCalls==0&&handler.LyricCalls==1,"An exact suggestion detail avoids unnecessary singer-album traversal");
                    if(!restricted){await Task.Delay(20).ConfigureAwait(false);Check(handler.PrimaryCancelled&&handler.AlternateCancelled,"A native catalog winner cancels both slower title searches");}
                }
                var cachedHandler=new EarlyCatalogHandler();
                using(var repository=new LyricRepository(data,cachedHandler,true)){
                    var document=await repository.FindAsync(AvidTrack(),CancellationToken.None).ConfigureAwait(false);
                    Check(document.HasTimedLyrics&&document.Source.Contains("缓存")&&cachedHandler.SearchCalls==0&&cachedHandler.CatalogCalls==0,"Early native catalog result reopens from the verified recording cache (restricted="+restricted+", source="+document.Source+", searches="+cachedHandler.SearchCalls+", catalogs="+cachedHandler.CatalogCalls+")");
                }
            }
            foreach(int mismatch in new[]{0,1,2}){
                var handler=new EarlyCatalogHandler{Restricted=true,WrongAlbum=mismatch==0,WrongDuration=mismatch==1,Ambiguous=mismatch==2};
                using(var repository=new LyricRepository(Path.Combine(directory,"unsafe-"+mismatch),handler,true)){
                    bool rejected=false;try{await repository.FindAsync(AvidTrack(),CancellationToken.None).ConfigureAwait(false);}catch(InvalidOperationException){rejected=true;}
                    Check(rejected&&handler.LyricCalls==0,"Early catalog recovery rejects "+(mismatch==0?"another album":mismatch==1?"another duration":"ambiguous same-album recordings"));
                }
            }
            var cancelledHandler=new EarlyCatalogHandler{SlowCatalog=true};
            using(var repository=new LyricRepository(Path.Combine(directory,"cancelled"),cancelledHandler,true))
            using(var cancellation=new CancellationTokenSource()){
                var pending=repository.FindAsync(AvidTrack(),cancellation.Token);
                await Ready(cancelledHandler.CatalogStarted.Task,"Slow native catalog starts before ordinary search deadlines");
                cancellation.Cancel();await Ready(pending,"Changing track promptly cancels all three pending catalog searches");
                bool cancelled=false;try{await pending.ConfigureAwait(false);}catch(OperationCanceledException){cancelled=true;}
                Check(cancelled&&cancelledHandler.LyricCalls==0,"A cancelled early catalog never fetches or binds a stale song");
            }
            var fastHandler=new EarlyCatalogHandler{FastPrimary=true};
            using(var repository=new LyricRepository(Path.Combine(directory,"fast-primary"),fastHandler,true)){
                var document=await repository.FindAsync(AvidTrack(),CancellationToken.None).ConfigureAwait(false);
                Check(document.HasTimedLyrics&&fastHandler.SearchCalls==1&&fastHandler.CatalogCalls==0,"Fast ordinary QQ lookup avoids both extra search paths");
            }
        }finally{DeleteTemporaryDirectory(directory);}
    }
    static void NativeControllerSwap(){
        string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"qq-native-controller-"+Guid.NewGuid().ToString("N"));
        try{
            var store=new SettingsStore(directory);
            using(var repository=new LyricRepository(store.DataDirectory,new BilingualAlbumHandler()))
            using(var controller=new LyricController(new AppSettings{HideInstrumental=false},store,repository)){
                var snapshot=RefrainTrack();snapshot.HasTimeline=true;snapshot.PositionSeconds=1.5;snapshot.IsPlaying=false;
                controller.ApplySnapshot(snapshot,DateTime.UtcNow);WaitForLoad(controller);
                var initial=controller.Document;var native=LrcParser.Parse("[00:01]Native original\n[00:05]Native next");native.Source="QQ 音乐";native.Translation="[00:01]原平台译文\n[00:05]原平台下一句";LrcParser.EnsureTranslation(native);
                initial.NativeReplacement=native;controller.ApplySnapshot(snapshot,DateTime.UtcNow);
                Check(controller.Document==native&&controller.Current=="Native original"&&controller.CurrentTranslation=="原平台译文","Controller atomically replaces both lyric rows on its normal UI update");
                var manual=LrcParser.Parse("[00:01]Manual original");manual.Translation="[00:01]手动译文";controller.ApplyDocument(manual);manual.NativeReplacement=native;controller.ApplySnapshot(snapshot,DateTime.UtcNow);
                Check(controller.Document==manual&&controller.Current=="Manual original"&&controller.CurrentTranslation=="手动译文","Native enrichment cannot override an explicitly selected manual lyric");
            }
        }finally{DeleteTemporaryDirectory(directory);}
    }
    static void DisplayFormats(){
        foreach(var pair in new[]{new[]{"Small Song","小小的歌"},new[]{"宇宙","宇宙之歌"},new[]{"별빛","星光"}}){
            string title=pair[0],alias=pair[1];
            var observed=Track(title+"（"+alias+"）","Small Album (小专辑)",180);
            observed.Artist="J.Fla (Kim Jung Hwa)";
            var candidate=Song("7",title,"Small Album",180);candidate.Artist="J.Fla";
            candidate.TitleAliases.Add(alias);candidate.AlbumAliases.Add("小专辑");
            Check(LyricRepository.QQSearchTitle(observed.Title)==title,"Query simplifies translated display text across scripts: "+title);
            Check(Match(observed,candidate),"Official title and album aliases match across scripts: "+title);
            observed.Title=alias;observed.Album="小专辑";
            Check(Match(observed,candidate),"Catalog alias by itself retains full recording identity: "+title);
            observed.Title=title+" ("+alias+")";observed.Album="Small Album (小专辑)";
            candidate.TitleAliases.Clear();Check(!Match(observed,candidate),"English, kanji and Korean translations cannot be guessed: "+title);
            candidate.TitleAliases.Add(alias);candidate.AlbumAliases.Clear();Check(!Match(observed,candidate),"Unconfirmed album translation cannot compensate: "+title);
            candidate.AlbumAliases.Add("小专辑");candidate.Artist="Different artist";Check(!Match(observed,candidate),"Official translated title still rejects a cover: "+title);
            candidate.Artist="J.Fla / Guest";Check(!Match(observed,candidate),"Official translated title still rejects a co-performer: "+title);
            candidate.Artist="J.Fla";candidate.DurationSeconds=190;Check(!Match(observed,candidate),"Official translated title rejects the wrong duration: "+title);
            candidate.DurationSeconds=double.NaN;Check(!Match(observed,candidate),"Invalid catalog duration cannot pass identity checks: "+title);
            candidate.DurationSeconds=180;candidate.Album="Other Album";candidate.AlbumAliases.Clear();Check(!Match(observed,candidate),"Official translated title rejects the wrong album: "+title);
            candidate.Album="Small Album";candidate.AlbumAliases.Add("小专辑");
            foreach(int missing in new[]{0,1,2}){
                var incomplete=Track(observed.Title,observed.Album,180);incomplete.Artist=observed.Artist;
                if(missing==0)incomplete.Artist="";if(missing==1)incomplete.Album="";if(missing==2)incomplete.DurationSeconds=0;
                Check(!Match(incomplete,candidate),"Display aliases require all recording metadata: "+title+"/"+missing);
            }
            observed.Artist="J.Fla";
            var other=Song("8",title,"Small Album",181.8);other.Artist="J.Fla";
            other.TitleAliases.Add(alias);other.AlbumAliases.Add("小专辑");
            Check(!Match(observed,candidate,other),"A closer duration cannot resolve two translated recordings: "+title);
            observed.Player=MusicPlayer.NetEase;
            Check(LyricRepository.SelectAutomaticMatch(observed,new[]{candidate})==null,"Native NetEase identity does not inherit QQ display compensation: "+title);
        }
        foreach(string version in new[]{"Live","DJ","Cover","Remix","Explicit","现场版","伴奏"}){
            var candidate=Song("9","Small Song ("+version+")","Small Album",180);candidate.TitleAliases.Add("小小的歌");
            Check(!Match(Track("Small Song (小小的歌)","Small Album",180),candidate),"Catalog aliases cannot erase a candidate recording version: "+version);
            candidate.Title="Small Song";candidate.TitleAliases.Clear();candidate.TitleAliases.Add(version);
            var observed=Track("Small Song ("+version+")","Small Album",180);
            Check(!Match(observed,candidate),"Catalog aliases cannot erase the observed recording version: "+version);
            Check(LyricRepository.QQSearchTitle(observed.Title)==observed.Title,"Search preserves the observed recording version: "+version);
        }
    }
    sealed class CatalogHandler:HttpMessageHandler{
        internal int Calls;
        internal readonly string AliasKey,Title;
        internal CatalogHandler(string key,string title){AliasKey=key;Title=title;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
            Calls++;string body;
            if(request.RequestUri.Host=="u.y.qq.com")body="{\"code\":0,\"request\":{\"code\":2001}}";
            else if(request.RequestUri.AbsolutePath=="/api/search/get/web"){
                string query=Uri.UnescapeDataString(request.RequestUri.Query);
                Check(query.Contains(Title+" J.Fla")&&!query.Contains("Kim Jung Hwa")&&!query.Contains("小小的歌"),"Catalog fallback query uses undecorated title and artist for "+AliasKey);
                bool compact=AliasKey=="tns"||AliasKey=="alia";
                var album=new Dictionary<string,object>{{"name","Small Album"},{AliasKey,new[]{"小专辑"}}};
                var song=new Dictionary<string,object>{{"id",123456},{"name",Title},{compact?"dt":"duration",180000},{compact?"ar":"artists",new[]{new{name="J.Fla"}}},{compact?"al":"album",album},{AliasKey,new[]{"小小的歌","小小的歌",""}}};
                body=new JavaScriptSerializer().Serialize(new{code=200,result=new{songs=new[]{song}}});
            }else if(request.RequestUri.AbsolutePath=="/api/song/lyric"){
                body="{\"code\":200,\"lrc\":{\"lyric\":\"[00:01]Original lyric\\n[00:03]Next line\"},\"tlyric\":{\"lyric\":\"[00:01]原文译文\\n[00:03]下一句\"}}";
            }else throw new Exception("Unexpected catalog fallback endpoint");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")});
        }
    }
    static async Task CatalogFallback(){
        string root=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory),directory=Path.Combine(root,"qq-catalog-"+Guid.NewGuid().ToString("N"));
        try{
            foreach(string key in new[]{"transNames","tns","alias","alia"}){
                string title=key=="tns"?"宇宙":"Small Song",data=Path.Combine(directory,key);
                var handler=new CatalogHandler(key,title);
                var observed=Track(title+" (小小的歌)","Small Album (小专辑)",180);observed.Player=MusicPlayer.QQMusic;observed.Artist="J.Fla (Kim Jung Hwa)";
                using(var repository=new LyricRepository(data,handler)){
                    var document=await repository.FindAsync(observed,CancellationToken.None).ConfigureAwait(false);
                    Check(document!=null&&document.HasTimedLyrics&&document.HasTranslation&&handler.Calls==3,"Catalog title/album alias parser retrieves bilingual fallback through "+key);
                }
                string[] bindings=Directory.GetFiles(Path.Combine(data,"lyric-match-v1"),"*.xml");
                Check(bindings.Length==1,"Catalog alias result writes one recording binding: "+key);
                var xml=new XmlDocument();xml.Load(bindings[0]);
                Check(xml.SelectNodes("match/titleAliases/alias").Count==1&&xml.SelectNodes("match/albumAliases/alias").Count==1,"Catalog aliases are deduplicated and persisted: "+key);
                handler=new CatalogHandler(key,title);
                using(var repository=new LyricRepository(data,handler)){
                    var document=await repository.FindAsync(observed,CancellationToken.None).ConfigureAwait(false);
                    Check(document!=null&&document.HasTranslation&&document.Source.Contains("缓存")&&handler.Calls==0,"Reopening reuses bilingual catalog binding with no network: "+key);
                }
            }
        }finally{
            if(!Path.GetFullPath(directory).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Invalid catalog cleanup path");
            DeleteTemporaryDirectory(directory);
        }
    }
    sealed class AliasHandler:HttpMessageHandler {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
            Calls++;string body;
            if(request.RequestUri.Host=="u.y.qq.com")body="{\"code\":0,\"request\":{\"code\":2001}}";
            else if(request.RequestUri.AbsolutePath=="/api/search/get/web"){
                string query=Uri.UnescapeDataString(request.RequestUri.Query);
                Check(query.Contains("悪魔の子 ヒグチアイ")&&!query.Contains("HiguchiAi"),"Automatic search uses the canonical artist display name");
                body="{\"code\":200,\"result\":{\"songs\":[{\"id\":1910623420,\"name\":\"悪魔の子\",\"duration\":227679,\"artists\":[{\"name\":\"ヒグチアイ\"}],\"album\":{\"name\":\"悪魔の子\"}},{\"id\":1925436021,\"name\":\"悪魔の子\",\"duration\":229960,\"artists\":[{\"name\":\"ヒグチアイ\"}],\"album\":{\"name\":\"最悪最愛\"}}]}}";
            }else if(request.RequestUri.AbsolutePath=="/api/song/lyric"){
                Check(request.RequestUri.Query.Contains("id=1910623420"),"Fallback fetches the exact album recording ID");
                body="{\"code\":200,\"lrc\":{\"lyric\":\"[00:00]悪魔の子\\n[00:02]Next\"},\"tlyric\":{\"lyric\":\"[00:00]恶魔之子\\n[00:02]下一句\"}}";
            }else throw new Exception("Artist alias lookup must not call restricted legacy search");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")});
        }
    }
    static async Task AliasFallback(){
        string root=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory),directory=Path.Combine(root,"qq-alias-"+Guid.NewGuid().ToString("N"));
        try{
            var handler=new AliasHandler();using(var repository=new LyricRepository(directory,handler)){
                var observed=AkumaTrack();string key=observed.PlaybackKey;
                var document=await repository.FindAsync(observed,CancellationToken.None).ConfigureAwait(false);
                Check(document!=null&&document.HasTimedLyrics&&document.HasTranslation&&handler.Calls==3,"Restricted QQ request recovers both languages through the verified alias recording");
                Check(observed.PlaybackKey==key&&observed.Artist=="ヒグチアイ (HiguchiAi)","Matching leaves player metadata and local binding keys unchanged");
                document=await repository.FindAsync(observed,CancellationToken.None).ConfigureAwait(false);
                Check(document!=null&&document.Source.Contains("缓存")&&document.HasTranslation&&handler.Calls==3,"Alias match and bilingual cache are reused without another restricted request");
            }
        }finally{
            if(!Path.GetFullPath(directory).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Invalid alias cleanup path");
            DeleteTemporaryDirectory(directory);
        }
    }
    sealed class Handler:HttpMessageHandler{
        internal readonly List<string> Calls=new List<string>();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
            Calls.Add(request.RequestUri.Host+request.RequestUri.AbsolutePath);
            string body;
            if(request.RequestUri.Host=="u.y.qq.com")body="{\"code\":0,\"request\":{\"code\":2001}}";
            else if(request.RequestUri.AbsolutePath=="/api/search/get/web"){
                Check(Uri.UnescapeDataString(request.RequestUri.Query).Contains("夜に駆ける YOASOBI"),"Search uses original Japanese name and artist");
                body="{\"code\":200,\"result\":{\"songs\":[{\"id\":1409311773,\"name\":\"夜に駆ける\",\"duration\":261013,\"artists\":[{\"name\":\"YOASOBI\"}],\"album\":{\"name\":\"夜に駆ける\"}}]}}";
            }else if(request.RequestUri.AbsolutePath=="/api/song/lyric")body="{\"code\":200,\"lrc\":{\"lyric\":\"[00:00]沈むように\\n[00:02]Next\"},\"tlyric\":{\"lyric\":\"[00:00]仿佛沉没一般\\n[00:02]下一句\"}}";
            else throw new Exception("Unexpected endpoint; restricted QQ search must not call legacy service.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")});
        }
    }
    static async Task Fallback(){
        string root=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory),directory=Path.Combine(root,"qq-matching-"+Guid.NewGuid().ToString("N"));
        try{
            var handler=new Handler();using(var repository=new LyricRepository(directory,handler)){
                var observed=Track("夜に駆ける (向夜晚奔去)","夜に駆ける (向夜晚奔去)");
                var document=await repository.FindAsync(observed,CancellationToken.None);
                Check(document!=null&&document.HasTimedLyrics&&document.HasTranslation&&document.Source.Contains("QQ 音乐备用歌词"),"Restricted search uses original and translation of verified fallback recording");
                Check(handler.Calls.Count==3,"One restricted search, one exact fallback search and one lyric request");
                document=await repository.FindAsync(observed,CancellationToken.None);
                Check(document!=null&&document.Source.Contains("缓存")&&handler.Calls.Count==3,"Bilingual recording binding is reused without network");
                observed.DurationSeconds=240;
                bool unmatched=false;try{document=await repository.FindAsync(observed,CancellationToken.None);}catch(InvalidOperationException error){unmatched=error.Message.Contains("2001");}
                Check(unmatched&&handler.Calls.Count==5,"Changed duration cannot reuse binding or fetch different lyrics");
                using(var cancellation=new CancellationTokenSource()){
                    cancellation.Cancel();bool cancelled=false;try{await repository.FindAsync(observed,cancellation.Token);}catch(OperationCanceledException){cancelled=true;}
                    Check(cancelled&&handler.Calls.Count==5,"Cancelled lookup makes no request");
                }
            }
        }finally{
            if(!Path.GetFullPath(directory).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Invalid test cleanup path");
            DeleteTemporaryDirectory(directory);
        }
    }

    sealed class RecoveryHandler:HttpMessageHandler {
        internal int SearchCalls, FailuresRemaining;
        internal bool NoMatch, AlternateFails, EmptyLyrics;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
            token.ThrowIfCancellationRequested();
            string body;
            if(request.RequestUri.Host=="u.y.qq.com" && Uri.UnescapeDataString(request.RequestUri.Query).Contains("GetPlayLyricInfo")) {
                body="{\"code\":0,\"lyrics\":{\"code\":0,\"data\":{\"lyric\":\"\",\"trans\":\"\"}}}";
            } else if(request.RequestUri.Host=="u.y.qq.com") {
                SearchCalls++;
                if(FailuresRemaining>0) { FailuresRemaining--; body="{\"code\":0,\"request\":{\"code\":2001}}"; }
                else body="{\"code\":0,\"request\":{\"code\":0,\"data\":{\"body\":{\"song\":{\"list\":"+(NoMatch?"[]":"[{\"mid\":\"retryfixture\",\"title\":\"Retry song\",\"interval\":120,\"singer\":[{\"name\":\"Retry artist\"}],\"album\":{\"name\":\"Retry album\"}}]")+"}}}}}";
            } else if(request.RequestUri.AbsolutePath=="/api/search/get/web") {
                if(AlternateFails) throw new HttpRequestException("Fixture service is temporarily unavailable");
                body="{\"code\":200,\"result\":{\"songs\":[]}}";
            } else if(request.RequestUri.AbsolutePath=="/soso/fcgi-bin/client_search_cp") body="{\"code\":0,\"data\":{\"song\":{\"list\":[]}}}";
            else if(request.RequestUri.AbsolutePath=="/lyric/fcgi-bin/fcg_query_lyric_new.fcg") {
                string lrc=Convert.ToBase64String(Encoding.UTF8.GetBytes(EmptyLyrics?"":"[00:00]Recovered lyrics\n[01:00]Next line"));
                string trans=Convert.ToBase64String(Encoding.UTF8.GetBytes(EmptyLyrics?"":"[00:00]恢复译文\n[01:00]下一句"));
                body="{\"code\":0,\"lyric\":\""+lrc+"\",\"trans\":\""+trans+"\"}";
            } else throw new Exception("Unexpected recovery fixture endpoint");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")});
        }
    }
    static MusicSnapshot RecoveryTrack(bool playing=true) {
        return new MusicSnapshot { Player=MusicPlayer.QQMusic,Title="Retry song",Artist="Retry artist",Album="Retry album",DurationSeconds=120,HasTimeline=true,PositionSeconds=10,IsPlaying=playing };
    }
    static void WaitForLoad(LyricController controller) {
        SynchronizationContext context=SynchronizationContext.Current;
        DateTime deadline=DateTime.UtcNow.AddSeconds(5);
        // DoEvents temporarily enters/exits a message loop and can uninstall the
        // WinForms context. Keep subsequent fixture loads on the same UI thread.
        while(controller.Searching&&DateTime.UtcNow<deadline) { Application.DoEvents(); SynchronizationContext.SetSynchronizationContext(context); Thread.Sleep(1); }
        Application.DoEvents();SynchronizationContext.SetSynchronizationContext(context);
        Check(!controller.Searching,"Fixture lyric operation completed");
    }
    static void ControllerRecovery() {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        string root=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory),directory=Path.Combine(root,"qq-recovery-"+Guid.NewGuid().ToString("N"));
        try {
            var handler=new RecoveryHandler { FailuresRemaining=5 };
            var store=new SettingsStore(Path.Combine(directory,"transient"));
            using(var repository=new LyricRepository(store.DataDirectory,handler))
            using(var controller=new LyricController(new AppSettings { HideInstrumental=false },store,repository)) {
                DateTime started=DateTime.UtcNow;
                controller.ApplySnapshot(RecoveryTrack(),started); WaitForLoad(controller);
                Check(controller.Document==null&&controller.IsSongInfo,"Unavailable search displays current song information");
                Check(controller.NextLyricRetryUtc>started.AddSeconds(1.5)&&controller.NextLyricRetryUtc<started.AddSeconds(3),"First transient failure no longer waits eight seconds");
                DateTime due=controller.NextLyricRetryUtc;
                controller.ApplySnapshot(RecoveryTrack(),due.AddMilliseconds(-1));
                Check(handler.SearchCalls==1,"Normal player polling does not bypass retry deadline");
                controller.ApplySnapshot(RecoveryTrack(false),due.AddSeconds(1));
                Check(handler.SearchCalls==1,"Paused player defers automatic network retries");
                for(int i=0;i<5;i++) {
                    Check(controller.NextLyricRetryUtc!=DateTime.MaxValue,"Service failure remains recoverable after three attempts");
                    controller.ApplySnapshot(RecoveryTrack(),controller.NextLyricRetryUtc.AddMilliseconds(1)); WaitForLoad(controller);
                }
                Check(handler.SearchCalls==6&&controller.Current=="Recovered lyrics"&&controller.CurrentTranslation=="恢复译文","Repeated 2001 responses recover automatically with actual timed original and translation (requests="+handler.SearchCalls+", current="+controller.Current+", translation="+controller.CurrentTranslation+", status="+controller.Message+")");
                controller.ApplySnapshot(RecoveryTrack(),DateTime.UtcNow.AddHours(1));
                Check(handler.SearchCalls==6,"A loaded document ends automatic network retries");
                controller.Reload(); WaitForLoad(controller);
                Check(handler.SearchCalls==6&&controller.Document.Source.Contains("缓存"),"Reload reuses validated lyrics without another search");
            }

            handler=new RecoveryHandler { NoMatch=true };
            store=new SettingsStore(Path.Combine(directory,"unmatched"));
            using(var repository=new LyricRepository(store.DataDirectory,handler))
            using(var controller=new LyricController(new AppSettings { HideInstrumental=false },store,repository)) {
                controller.ApplySnapshot(RecoveryTrack(),DateTime.UtcNow); WaitForLoad(controller);
                for(int i=0;i<2;i++) { controller.ApplySnapshot(RecoveryTrack(),controller.NextLyricRetryUtc.AddMilliseconds(1)); WaitForLoad(controller); }
                controller.ApplySnapshot(RecoveryTrack(),DateTime.UtcNow.AddHours(1));
                Check(handler.SearchCalls==3&&controller.Document==null&&controller.NextLyricRetryUtc==DateTime.MaxValue,"Successful searches without a safe identity still stop after three attempts");
            }

            handler=new RecoveryHandler { NoMatch=true,AlternateFails=true };
            store=new SettingsStore(Path.Combine(directory,"alternate-down"));
            using(var repository=new LyricRepository(store.DataDirectory,handler))
            using(var controller=new LyricController(new AppSettings { HideInstrumental=false },store,repository)) {
                controller.ApplySnapshot(RecoveryTrack(),DateTime.UtcNow); WaitForLoad(controller);
                for(int i=0;i<3;i++) { controller.ApplySnapshot(RecoveryTrack(),controller.NextLyricRetryUtc.AddMilliseconds(1)); WaitForLoad(controller); }
                Check(handler.SearchCalls==4&&controller.NextLyricRetryUtc!=DateTime.MaxValue&&controller.Message.Contains("接口暂时不可用"),"An unavailable alternative provider is not misreported as a conclusive missing match");
                controller.Settings.OnlineLyrics=false;
                controller.ApplySnapshot(RecoveryTrack(),controller.NextLyricRetryUtc.AddSeconds(1));
                Check(handler.SearchCalls==4,"Disabling online lyrics prevents pending retries");
                controller.Settings.OnlineLyrics=true;
                controller.Settings.QQEnabled=false;
                controller.ApplySnapshot(RecoveryTrack(),controller.NextLyricRetryUtc.AddSeconds(1));
                Check(handler.SearchCalls==4&&!controller.Snapshot.HasTrack,"Disabling QQ clears the pending track without another request");
            }

            handler=new RecoveryHandler { EmptyLyrics=true };
            store=new SettingsStore(Path.Combine(directory,"empty"));
            using(var repository=new LyricRepository(store.DataDirectory,handler))
            using(var controller=new LyricController(new AppSettings { HideInstrumental=false },store,repository)) {
                controller.ApplySnapshot(RecoveryTrack(),DateTime.UtcNow); WaitForLoad(controller);
                controller.ApplySnapshot(RecoveryTrack(),DateTime.UtcNow.AddHours(1));
                Check(handler.SearchCalls==1&&controller.Document!=null&&!controller.Document.HasTimedLyrics&&controller.Message.Contains("平台暂未提供歌词"),"A genuine platform response without lyrics is not retried as a network outage");
            }
        } finally {
            if(!Path.GetFullPath(directory).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Invalid recovery cleanup path");
            DeleteTemporaryDirectory(directory);
        }
    }

    sealed class LoadingHandler:HttpMessageHandler {
        internal bool BlockPrimary=true, PrimaryCancelled;
        internal int PrimaryCalls, AlternateCalls, AlternateFetches;
        internal string AlternateAlbum="Retry album", AlternateTitle="Retry song";
        internal readonly TaskCompletionSource<HttpResponseMessage> PendingPrimary=new TaskCompletionSource<HttpResponseMessage>();
        internal static HttpResponseMessage Reply(string body) {
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body,Encoding.UTF8,"application/json")};
        }
        internal static string PrimarySong {
            get { return "{\"code\":0,\"request\":{\"code\":0,\"data\":{\"body\":{\"song\":{\"list\":[{\"mid\":\"loadingfixture\",\"title\":\"Retry song\",\"interval\":120,\"singer\":[{\"name\":\"Retry artist\"}],\"album\":{\"name\":\"Retry album\"}}]}}}}}"; }
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
            token.ThrowIfCancellationRequested();
            if(request.RequestUri.Host=="u.y.qq.com") {
                if(Uri.UnescapeDataString(request.RequestUri.Query).Contains("GetPlayLyricInfo"))
                    return Task.FromResult(Reply("{\"code\":0,\"lyrics\":{\"code\":0,\"data\":{\"lyric\":\"\",\"trans\":\"\"}}}"));
                PrimaryCalls++;
                if(!BlockPrimary) return Task.FromResult(Reply(PrimarySong));
                var registration=token.Register(delegate { PrimaryCancelled=true; PendingPrimary.TrySetCanceled(); });
                PendingPrimary.Task.ContinueWith(delegate { registration.Dispose(); },TaskScheduler.Default);
                return PendingPrimary.Task;
            }
            if(request.RequestUri.AbsolutePath=="/api/search/get/web") {
                AlternateCalls++;
                return Task.FromResult(Reply("{\"code\":200,\"result\":{\"songs\":[{\"id\":9001,\"name\":\""+AlternateTitle+"\",\"duration\":120000,\"artists\":[{\"name\":\"Retry artist\"}],\"album\":{\"name\":\""+AlternateAlbum+"\"}}]}}"));
            }
            if(request.RequestUri.AbsolutePath=="/api/song/lyric") {
                AlternateFetches++;
                return Task.FromResult(Reply("{\"code\":200,\"lrc\":{\"lyric\":\"[00:00]Recovered lyrics\\n[01:00]Next line\"},\"tlyric\":{\"lyric\":\"[00:00]恢复译文\\n[01:00]下一句\"}}"));
            }
            if(request.RequestUri.AbsolutePath=="/lyric/fcgi-bin/fcg_query_lyric_new.fcg") {
                string lrc=Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:00]Recovered lyrics\n[01:00]Next line"));
                string trans=Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:00]恢复译文\n[01:00]下一句"));
                return Task.FromResult(Reply("{\"code\":0,\"lyric\":\""+lrc+"\",\"trans\":\""+trans+"\"}"));
            }
            throw new Exception("Unexpected loading fixture endpoint");
        }
    }
    static async Task ParallelLoading() {
        string root=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory),directory=Path.Combine(root,"qq-parallel-"+Guid.NewGuid().ToString("N"));
        try {
            var handler=new LoadingHandler();
            using(var repository=new LyricRepository(Path.Combine(directory,"slow"),handler)) {
                var watch=Stopwatch.StartNew();
                var result=await repository.FindAsync(RecoveryTrack(),CancellationToken.None).ConfigureAwait(false);
                Check(result!=null&&result.HasTimedLyrics&&result.HasTranslation&&result.Source.Contains("备用歌词")&&watch.Elapsed.TotalSeconds<3,"Verified alternative loads before a blocked primary search finishes");
                Check(handler.PrimaryCancelled&&handler.AlternateCalls==1&&handler.AlternateFetches==1,"Winning alternate lookup cancels the blocked request");
                result=await repository.FindAsync(RecoveryTrack(),CancellationToken.None).ConfigureAwait(false);
                Check(result.Source.Contains("缓存")&&handler.PrimaryCalls==1&&handler.AlternateCalls==1,"Alternative binding and translation reload entirely from cache");
            }
            handler=new LoadingHandler { BlockPrimary=false };
            using(var repository=new LyricRepository(Path.Combine(directory,"fast"),handler)) {
                var result=await repository.FindAsync(RecoveryTrack(),CancellationToken.None).ConfigureAwait(false);
                Check(result.Source.StartsWith("QQ 音乐")&&handler.AlternateCalls==0,"A fast primary response avoids the alternate request");
            }
            foreach(bool wrongVersion in new[]{false,true}) {
                handler=new LoadingHandler { AlternateAlbum=wrongVersion?"Retry album":"Different album",AlternateTitle=wrongVersion?"Retry song (Live)":"Retry song" };
                using(var repository=new LyricRepository(Path.Combine(directory,wrongVersion?"version":"album"),handler)) {
                    Task<LyricDocument> pending=repository.FindAsync(RecoveryTrack(),CancellationToken.None);
                    DateTime deadline=DateTime.UtcNow.AddSeconds(3);
                    while(handler.AlternateCalls==0&&DateTime.UtcNow<deadline)await Task.Delay(10).ConfigureAwait(false);
                    Check(handler.AlternateCalls==1&&!pending.IsCompleted&&handler.AlternateFetches==0,"Racing providers does not accept a wrong "+(wrongVersion?"version":"album"));
                    handler.PendingPrimary.TrySetResult(LoadingHandler.Reply(LoadingHandler.PrimarySong));
                    var result=await pending.ConfigureAwait(false);
                    Check(result.Source.StartsWith("QQ 音乐")&&!result.Source.Contains("备用歌词"),"Exact primary wins after rejecting unsafe alternate identity");
                }
            }
        } finally {
            if(!Path.GetFullPath(directory).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Invalid parallel cleanup path");
            DeleteTemporaryDirectory(directory);
        }
    }
    static void RefreshContinuity() {
        string root=Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory),directory=Path.Combine(root,"qq-refresh-"+Guid.NewGuid().ToString("N"));
        try {
            var handler=new LoadingHandler { BlockPrimary=false,AlternateAlbum="Different album" };
            var store=new SettingsStore(directory);
            using(var repository=new LyricRepository(store.DataDirectory,handler))
            using(var controller=new LyricController(new AppSettings { HideInstrumental=false },store,repository)) {
                controller.ApplySnapshot(RecoveryTrack(),DateTime.UtcNow); WaitForLoad(controller);
                LyricDocument connected=controller.Document;
                foreach(string file in Directory.GetFiles(Path.Combine(store.DataDirectory,"lyric-cache-v1"),"*.xml"))File.WriteAllText(file,"<invalid />");
                handler.BlockPrimary=true;
                controller.Reload();
                Check(controller.Searching&&controller.Document==connected&&controller.Current=="Recovered lyrics"&&controller.CurrentTranslation=="恢复译文","Reload keeps both connected languages visible while the request is delayed");
                int requests=handler.PrimaryCalls;
                controller.Reload();
                Check(handler.PrimaryCalls==requests,"Repeated refresh clicks reuse the in-flight operation");
                handler.PendingPrimary.TrySetResult(LoadingHandler.Reply("{\"code\":0,\"request\":{\"code\":2001}}"));
                WaitForLoad(controller);
                Check(controller.Document==connected&&controller.Current=="Recovered lyrics"&&controller.Message.Contains("继续显示"),"Failed refresh retains usable current lyrics");
                var next=RecoveryTrack(); next.Title="Changed song";
                controller.ApplySnapshot(next,DateTime.UtcNow); WaitForLoad(controller);
                Check(controller.Document==null&&controller.Current.Contains("Changed song")&&!controller.Current.Contains("Recovered lyrics"),"Same-song refresh retention never carries the old document across a song change");
            }
            handler=new LoadingHandler();
            store=new SettingsStore(Path.Combine(directory,"late-timeline"));
            using(var repository=new LyricRepository(store.DataDirectory,handler))
            using(var controller=new LyricController(new AppSettings { HideInstrumental=false },store,repository)) {
                var partial=RecoveryTrack(); partial.HasTimeline=false; partial.DurationSeconds=0;
                controller.ApplySnapshot(partial,DateTime.UtcNow);
                Check(controller.Searching&&controller.Document==null,"Initial missing timeline can have a pending lyric request");
                handler.BlockPrimary=false;
                controller.ApplySnapshot(RecoveryTrack(),DateTime.UtcNow); WaitForLoad(controller);
                Check(handler.PrimaryCancelled&&handler.PrimaryCalls==2&&controller.Current=="Recovered lyrics","Fresh native duration cancels the incomplete lookup and immediately enables cache and safe provider matching");
            }
        } finally {
            if(!Path.GetFullPath(directory).StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Invalid refresh cleanup path");
            DeleteTemporaryDirectory(directory);
        }
    }
    sealed class CancelledNativeHandler:HttpMessageHandler {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
            Calls++;
            if(Calls==1){var completion=new TaskCompletionSource<HttpResponseMessage>();completion.SetCanceled();return completion.Task;}
            return Task.FromResult(LoadingHandler.Reply("{\"code\":200,\"lrc\":{\"lyric\":\"[00:00]Recovered native lyrics\\n[01:00]Next line\"}}"));
        }
    }
    static void LateInformationRecovery() {
        string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"late-information-"+Guid.NewGuid().ToString("N"));
        try {
            var loading=new LoadingHandler();var store=new SettingsStore(Path.Combine(directory,"pending-zero-duration"));
            using(var repository=new LyricRepository(store.DataDirectory,loading))
            using(var controller=new LyricController(new AppSettings{HideInstrumental=false},store,repository)) {
                var partial=RecoveryTrack();partial.DurationSeconds=0; // HasTimeline is already true.
                controller.ApplySnapshot(partial,DateTime.UtcNow);
                for(int i=0;i<10;i++)controller.ApplySnapshot(partial,DateTime.UtcNow);
                Check(controller.Searching&&loading.PrimaryCalls==1,"An incomplete timeline does not cancel/restart on every poll");
                loading.BlockPrimary=false;
                controller.ApplySnapshot(RecoveryTrack(),DateTime.UtcNow);WaitForLoad(controller);
                Check(loading.PrimaryCancelled&&loading.PrimaryCalls==2&&controller.Document!=null,"Late valid duration recovers even when HasTimeline stayed true");
            }
            var recovery=new RecoveryHandler{NoMatch=true};store=new SettingsStore(Path.Combine(directory,"exhausted-incomplete"));
            using(var repository=new LyricRepository(store.DataDirectory,recovery))
            using(var controller=new LyricController(new AppSettings{HideInstrumental=false},store,repository)) {
                var partial=RecoveryTrack();partial.DurationSeconds=0;
                controller.ApplySnapshot(partial,DateTime.UtcNow);WaitForLoad(controller);
                for(int i=0;i<2;i++){controller.ApplySnapshot(partial,controller.NextLyricRetryUtc.AddMilliseconds(1));WaitForLoad(controller);}
                Check(recovery.SearchCalls==3&&controller.NextLyricRetryUtc==DateTime.MaxValue,"Incomplete unsuccessful search reaches its bounded budget");
                recovery.NoMatch=false;
                controller.ApplySnapshot(RecoveryTrack(false),DateTime.UtcNow);
                Check(recovery.SearchCalls==3,"Late information during pause waits for playback");
                controller.ApplySnapshot(RecoveryTrack(),DateTime.UtcNow);WaitForLoad(controller);
                Check(recovery.SearchCalls==4&&controller.Current=="Recovered lyrics","Late duration restores exhausted matching without revisiting the song");
            }
            recovery=new RecoveryHandler();store=new SettingsStore(Path.Combine(directory,"loaded-incomplete"));
            using(var repository=new LyricRepository(store.DataDirectory,recovery))
            using(var controller=new LyricController(new AppSettings{HideInstrumental=false},store,repository)) {
                var partial=RecoveryTrack();partial.DurationSeconds=0;
                controller.ApplySnapshot(partial,DateTime.UtcNow);WaitForLoad(controller);
                Check(controller.Document!=null,"An exact unambiguous primary title can load before duration arrives");
                var corrected=RecoveryTrack();corrected.DurationSeconds=180;
                controller.ApplySnapshot(corrected,DateTime.UtcNow);WaitForLoad(controller);
                Check(controller.Document==null&&recovery.SearchCalls==2,"New duration revalidates an automatic document and rejects the wrong recording");
                controller.ApplyDocument(LrcParser.Parse("[00:00]Manually bound lyrics\n[00:50]Tail"));
                corrected=RecoveryTrack();controller.ApplySnapshot(corrected,DateTime.UtcNow.AddSeconds(1));controller.ApplySnapshot(corrected,DateTime.UtcNow.AddSeconds(2));
                Check(controller.Current=="Manually bound lyrics"&&recovery.SearchCalls==2,"Late duration never replaces a user-confirmed lyric binding");
            }
            recovery=new RecoveryHandler();store=new SettingsStore(Path.Combine(directory,"corrected-duration"));
            using(var repository=new LyricRepository(store.DataDirectory,recovery))
            using(var controller=new LyricController(new AppSettings{HideInstrumental=false},store,repository)) {
                var wrong=RecoveryTrack();wrong.DurationSeconds=180;
                controller.ApplySnapshot(wrong,DateTime.UtcNow);WaitForLoad(controller);
                Check(controller.Document==null,"Duration belonging to another recording is still rejected");
                DateTime at=DateTime.UtcNow;controller.ApplySnapshot(RecoveryTrack(),at);
                for(int i=0;i<10;i++){var jitter=RecoveryTrack();jitter.DurationSeconds+=i*.005;controller.ApplySnapshot(jitter,at.AddMilliseconds(i*40));}
                Check(recovery.SearchCalls==1,"Corrected duration settles before restarting; tiny rounding never creates a request storm");
                controller.ApplySnapshot(RecoveryTrack(),at.AddMilliseconds(501));WaitForLoad(controller);
                Check(recovery.SearchCalls==2&&controller.Current=="Recovered lyrics","Settled corrected duration bypasses old no-match backoff safely");
                for(int i=0;i<10;i++){var jitter=RecoveryTrack();jitter.DurationSeconds+=i*.01;controller.ApplySnapshot(jitter,at.AddSeconds(1+i));}
                Check(recovery.SearchCalls==2,"Loaded lyrics remain attached through duration rounding changes");
            }
            var native=new CancelledNativeHandler();store=new SettingsStore(Path.Combine(directory,"native-timeout"));
            using(var repository=new LyricRepository(store.DataDirectory,native))
            using(var controller=new LyricController(new AppSettings{HideInstrumental=false},store,repository)) {
                var song=RecoveryTrack();song.Player=MusicPlayer.NetEase;song.PlatformTrackId="123";
                controller.ApplySnapshot(song,DateTime.UtcNow);WaitForLoad(controller);
                Check(controller.Document==null&&controller.NextLyricRetryUtc!=DateTime.MaxValue&&controller.Message.Contains("超时"),"Native request cancellation without cancelling the song token schedules recovery");
                controller.ApplySnapshot(song,controller.NextLyricRetryUtc.AddMilliseconds(1));WaitForLoad(controller);
                Check(native.Calls==2&&controller.Current=="Recovered native lyrics","NetEase native timeout recovers during the same song");
            }
            loading=new LoadingHandler();store=new SettingsStore(Path.Combine(directory,"player-switch"));
            using(var repository=new LyricRepository(store.DataDirectory,loading))
            using(var controller=new LyricController(new AppSettings{HideInstrumental=false},store,repository)) {
                controller.ApplySnapshot(RecoveryTrack(),DateTime.UtcNow);
                var song=RecoveryTrack();song.Player=MusicPlayer.NetEase;song.PlatformTrackId="9001";
                controller.ApplySnapshot(song,DateTime.UtcNow);WaitForLoad(controller);
                Check(loading.PrimaryCancelled&&controller.Snapshot.Player==MusicPlayer.NetEase&&controller.Document.Source.StartsWith("网易云"),"Switching from a pending QQ lookup connects only the NetEase recording");
                controller.ApplySnapshot(song,DateTime.UtcNow.AddHours(1));
                Check(controller.NextLyricRetryUtc==DateTime.MaxValue&&loading.PrimaryCalls==1,"A superseded cancellation never schedules an old-player retry");
            }
        } finally { DeleteTemporaryDirectory(directory); }
    }
}
