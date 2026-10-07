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
    public static int Main(){try{Names();ArtistNames();DisplayFormats();ProviderDuration();ProviderDurationFallback().GetAwaiter().GetResult();Fallback().GetAwaiter().GetResult();AliasFallback().GetAwaiter().GetResult();CatalogFallback().GetAwaiter().GetResult();ParallelLoading().GetAwaiter().GetResult();ControllerRecovery();RefreshContinuity();LateInformationRecovery();Console.WriteLine("QQ matching/recovery checks passed: "+passed);return 0;}catch(Exception error){Console.Error.WriteLine(error);return 1;}}
    static MusicSnapshot FancuoTrack(){return new MusicSnapshot{Player=MusicPlayer.QQMusic,Title="犯错",Artist="顾峰 / 斯琴高丽",Album="顾式情歌",DurationSeconds=193.463};}
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
            if(request.RequestUri.Host=="u.y.qq.com") {
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
