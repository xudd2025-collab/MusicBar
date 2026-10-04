using System;
using System.Collections.Generic;
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
        bad=Sample(20,200,"Playing");bad["title"]="";Check(NetEaseBridgeReader.ParseSample(bad,now)==null,"missing title rejected");
        Uri uri;Check(NetEaseBridgeReader.TrySocketUri("ws://127.0.0.1:9223/devtools/page/test",out uri),"local socket accepted");
        Check(!NetEaseBridgeReader.TrySocketUri("ws://example.com:9223/devtools/page/test",out uri),"external socket refused");
        Check(!NetEaseBridgeReader.TrySocketUri("ws://127.0.0.1:9224/devtools/page/test",out uri),"other port refused");
        Check(!NetEaseBridgeReader.TrySocketUri("ws://127.0.0.1:9223/devtools/browser/test",out uri),"browser management socket refused");
        Check(NetEaseBridgeReader.IsPlayerPage("orpheus://orpheus/pub/hybrid/index.html"),"NetEase app page accepted");
        Check(NetEaseBridgeReader.IsPlayerPage("orpheus://orpheus/pub/app.html"),"current installed app page accepted");
        Check(!NetEaseBridgeReader.IsPlayerPage("https://music.163.com/"),"web pages refused");
        Console.WriteLine("NetEase bridge checks passed: "+passed);
    }
}
