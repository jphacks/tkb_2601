using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Guardian.UnitySim
{
    /// <summary>Local text envelope; no network, microphone, speech synthesis or process launch.</summary>
    public sealed class GuardianLocalSpeechBridge : IGuardianSpeechRecognizer,IGuardianSpeechOutput
    {
        public string Root {get;}
        public GuardianLocalSpeechBridge(string root){Root=Path.GetFullPath(root);}
        public bool TryRead(out GuardianSpeechMessage message)
        {
            message=default;if(!Directory.Exists(Root))return false;
            string path;
            try {path=Directory.EnumerateFiles(Root,"*.request").OrderBy(p=>p,StringComparer.Ordinal).FirstOrDefault();}
            catch(IOException){return false;}
            catch(UnauthorizedAccessException){return false;}
            if(path==null)return false;
            double now=UnityEngine.Time.realtimeSinceStartupAsDouble;
            message=new GuardianSpeechMessage{Id=Path.GetFileNameWithoutExtension(path),ReceivedAt=now,Confidence=0,Text=""};
            try
            {
                var file=new FileInfo(path);
                if(file.Length>8192 || (DateTime.UtcNow-file.LastWriteTimeUtc).TotalSeconds>10 || file.LastWriteTimeUtc>DateTime.UtcNow.AddSeconds(1))return true;
                string[] lines=File.ReadAllLines(path,Encoding.UTF8);
                if(lines.Length!=3)return true;
                message.Id=lines[0];message.Confidence=float.Parse(lines[1],CultureInfo.InvariantCulture);message.Text=Encoding.UTF8.GetString(Convert.FromBase64String(lines[2]));
                return true;
            }
            catch(IOException){return true;}
            catch(UnauthorizedAccessException){return true;}
            catch(FormatException){return true;}
            finally {try {File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}}
        }
        public void Say(string text)
        {Directory.CreateDirectory(Root);File.WriteAllText(Path.Combine(Root,"reply.txt"),text,new UTF8Encoding(false));}
    }
}
