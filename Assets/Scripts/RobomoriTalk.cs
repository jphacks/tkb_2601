// Robomori talk: a person asks one robot a question and the robot answers from its own live state, without a
// language model. The question (typed, or speech already turned into text) is matched to the nearest of a few
// kinds of question by comparing two-character pieces with example questions. The answer is a list of fixed
// phrases (StreamingAssets/Robomori/voice/talk/talk.tsv), each recorded once with VOICEVOX, so the robot
// speaks instantly and nothing has to synthesise speech while the demo runs. Nothing the person says is
// executed or passed on; the only effect a question can have is a stop, which is always allowed. A request to
// move again is answered with "not by voice".
//
// Shared by Unity and the phone gateway (scripts/start-phone.ps1 compiles it with Windows PowerShell 5.1),
// so it is written in C# 5.
using System;
using System.Collections.Generic;
using System.Text;

namespace Robomori
{
    /// <summary>What a robot knows about itself.</summary>
    public sealed class RobotFacts
    {
        public string Id = "?";
        /// <summary>Normal, Warning (no soup) or Limited (empty dishes only).</summary>
        public string Condition = "Normal";
        public bool Stopped;
        /// <summary>Why it is stopped: estop (told to stop), safety (a person or thing is close), unsafe (driving cannot be checked).</summary>
        public string Cause = "";
        public double StoppedFor;
        /// <summary>soup, food, dishes or empty; Table is 1-based, 0 for none.</summary>
        public string Kind = "";
        public int Table;
        public int Served, Cleared, TookOver;
    }

    public sealed class TalkAnswer
    {
        public string Robot, Intent, Text;
        public string[] Keys;
        public double Score;
        /// <summary>The person asked the robot to stop (and it was moving).</summary>
        public bool Stop;
    }

    public static class Talk
    {
        private sealed class Kind
        {
            public string Name;
            public string[] Examples, Keywords;
        }

        // The order matters only for ties: earlier kinds win.
        private static readonly Kind[] Kinds =
        {
            new Kind { Name = "status", Examples = new[] { "今の状況を教えて", "現状について教えてください", "状況はどう", "調子はどう", "大丈夫", "どうしたの", "どんな感じ", "今どうなってるの", "状態を教えて", "元気", "止まってるの", "動いてるの" },
                Keywords = new[] { "状況", "現状", "調子", "状態", "大丈夫", "どうしたの", "元気", "様子" } },
            new Kind { Name = "why", Examples = new[] { "なんで止まってるの", "どうして", "理由を教えて", "なぜ", "何があったの", "なんでスープを運ばないの", "どうして片付けだけなの", "原因は" },
                Keywords = new[] { "なんで", "なぜ", "どうして", "理由", "何があった", "原因", "何で" } },
            new Kind { Name = "task", Examples = new[] { "今何を運んでるの", "どこに行くの", "何番テーブル", "今の仕事は何", "何をしてるの", "担当は", "仕事を教えて", "行き先を教えて" },
                Keywords = new[] { "運んで", "どこ", "テーブル", "仕事は", "してるの", "担当" } },
            new Kind { Name = "can", Examples = new[] { "何ができるの", "スープは運べる", "料理は運べる", "できることを教えて", "片付けはできる" },
                Keywords = new[] { "できる", "運べる" } },
            new Kind { Name = "stats", Examples = new[] { "何回運んだ", "今日の実績は", "どれくらい働いた", "何件配膳した", "引き継ぎは何回", "成績は", "実績を教えて", "回数を教えて" },
                Keywords = new[] { "何回", "実績", "何件", "働いた", "成績" } },
            new Kind { Name = "fix", Examples = new[] { "どうすればいい", "直すにはどうしたらいい", "点検したほうがいい", "修理が必要", "何をすれば直る", "私は何をすればいい", "直し方を教えて" },
                Keywords = new[] { "どうすれば", "どうしたら", "直す", "直る", "点検", "修理" } },
            new Kind { Name = "who", Examples = new[] { "あなたは誰", "名前は", "自己紹介して", "誰ですか" },
                Keywords = new[] { "誰", "名前", "自己紹介" } },
            new Kind { Name = "greet", Examples = new[] { "こんにちは", "おはよう", "こんばんは", "やあ", "はじめまして" },
                Keywords = new[] { "こんにちは", "おはよう", "こんばんは", "はじめまして" } },
            new Kind { Name = "thanks", Examples = new[] { "ありがとう", "お疲れ様", "ありがとうございます", "よくやった", "助かった" },
                Keywords = new[] { "ありがと", "お疲れ", "おつかれ", "助かった" } },
            new Kind { Name = "stop", Examples = new[] { "止まって", "止めて", "ストップ", "停止して", "今すぐ止まって" },
                Keywords = new[] { "止ま", "止め", "ストップ", "停止" } },
            new Kind { Name = "resume", Examples = new[] { "動いて", "再開して", "動き出して", "仕事に戻って", "もう動いていいよ" },
                Keywords = new[] { "動いて", "再開", "動き出", "戻って" } },
        };

        private static readonly string[] Letters = { "a", "b", "c", "d", "e" }, Readings = { "えー", "びー", "しー", "でぃー", "いー" };

        /// <summary>Every example question, for a speech recogniser's list of expected phrases.</summary>
        public static string[] ExampleQuestions()
        {
            var list = new List<string>();
            foreach (var kind in Kinds) list.AddRange(kind.Examples);
            return list.ToArray();
        }

        // ---- phrases (key -> words), loaded from talk.tsv ----

        private static Dictionary<string, string> phrases = new Dictionary<string, string>();

        /// <summary>Reads "key&lt;TAB&gt;words" lines; returns how many phrases there are.</summary>
        public static int LoadPhrases(string tsv)
        {
            var table = new Dictionary<string, string>();
            foreach (var raw in (tsv ?? "").Split('\n'))
            {
                string line = raw.TrimEnd('\r').TrimStart('﻿');
                int tab = line.IndexOf('\t');
                if (line.Length == 0 || line[0] == '#' || tab <= 0) continue;
                table[line.Substring(0, tab).Trim()] = line.Substring(tab + 1).Trim();
            }
            phrases = table;
            return table.Count;
        }

        public static bool HasPhrase(string key) { return phrases.ContainsKey(key); }

        /// <summary>The words of a list of phrases; unknown keys are left out.</summary>
        public static string TextOf(IEnumerable<string> keys)
        {
            var sb = new StringBuilder();
            foreach (var key in keys)
            {
                string words;
                if (phrases.TryGetValue(key, out words)) sb.Append(words);
            }
            return sb.ToString();
        }

        // ---- understanding ----

        /// <summary>The robot a question names ("ロボットB", "ビーは", "Bの"), or null.</summary>
        public static string NamedRobot(string question)
        {
            string text = Normalize(question);
            for (int i = 0; i < Letters.Length; i++)
            {
                string upper = Letters[i].ToUpperInvariant();
                if (text.Contains("ろぼっと" + Letters[i]) || text.Contains("ろぼっと" + Readings[i])) return upper;
                foreach (string name in new[] { Letters[i], Readings[i] })
                    foreach (string particle in new[] { "は", "の", "に", "さん", "くん", "ちゃん", "って" })
                        if (text.StartsWith(name + particle)) return upper;
            }
            // A lone latin letter in otherwise Japanese text ("今bは大丈夫").
            for (int i = 0; i < text.Length; i++)
            {
                int at = Array.IndexOf(Letters, text[i].ToString());
                bool alone = (i == 0 || !IsLatin(text[i - 1])) && (i + 1 == text.Length || !IsLatin(text[i + 1]));
                if (at >= 0 && alone) return Letters[at].ToUpperInvariant();
            }
            return null;
        }

        /// <summary>Nearest kind of question and how sure (about 0 to 1.25). Below 0.3 it is "unknown".</summary>
        public static string Classify(string question, out double score)
        {
            string text = WithoutName(Normalize(question));
            score = 0;
            string best = "unknown";
            if (text.Length == 0) return best;
            bool negated = text.Contains("ないで") || text.Contains("なくて") || text.Contains("ません");
            bool describing = text.Contains("てる") || text.Contains("ている");  // "止まってる" asks about a state; it is not an order
            foreach (var kind in Kinds)
            {
                double s = 0;
                foreach (var e in kind.Examples) s = Math.Max(s, Dice(text, Normalize(e)));
                foreach (var k in kind.Keywords) if (text.Contains(Normalize(k))) { s += .25; break; }
                if ((negated || describing) && (kind.Name == "stop" || kind.Name == "resume")) s -= .5;
                if (s > score) { score = s; best = kind.Name; }
            }
            return score < .3 ? "unknown" : best;
        }

        // ---- answering ----

        public static TalkAnswer Answer(string question, RobotFacts f)
        {
            double score;
            string intent = Classify(question, out score);
            var keys = new List<string>();
            string me = "me_" + f.Id;
            bool warning = f.Condition == "Warning", limited = f.Condition == "Limited";
            switch (intent)
            {
                case "status":
                    keys.Add(me);
                    if (f.Stopped) { keys.Add(Cause(f)); keys.Add(Since(f.StoppedFor)); keys.Add("wait_human"); }
                    else if (warning) { keys.Add("warn_cause"); keys.Add("warn_plan"); Job(f, keys); }
                    else if (limited) { keys.Add("limited_cause"); keys.Add("limited_plan"); }
                    else { keys.Add("ok"); Job(f, keys); }
                    break;
                case "why":
                    if (f.Stopped) { keys.Add(Cause(f)); keys.Add("wait_human"); }
                    else if (warning) { keys.Add("warn_cause"); keys.Add("warn_plan"); }
                    else if (limited) { keys.Add("limited_cause"); keys.Add("limited_plan"); }
                    else keys.Add("why_ok");
                    break;
                case "task":
                    if (f.Stopped) keys.Add("now_stopped");
                    Job(f, keys);
                    break;
                case "can":
                    keys.Add(f.Stopped ? "can_none" : limited ? "can_limited" : warning ? "can_warn" : "can_all");
                    break;
                case "stats":
                    keys.Add(me);
                    keys.Add("served"); keys.Add(Count(f.Served));
                    keys.Add("cleared"); keys.Add(Count(f.Cleared));
                    keys.Add("tookover"); keys.Add(Count(f.TookOver));
                    break;
                case "fix":
                    keys.Add(f.Stopped ? "fix_stopped" : warning || limited ? "fix_vib" : "fix_ok");
                    break;
                case "who":
                    keys.Add(me); keys.Add("who");
                    break;
                case "greet":
                    keys.Add("hello"); keys.Add(me); keys.Add("ask_me");
                    break;
                case "thanks":
                    keys.Add("thanks");
                    break;
                case "stop":
                    keys.Add(f.Stopped ? "already_stopped" : "stop_ok");
                    break;
                case "resume":
                    keys.Add("resume_no");
                    break;
                default:
                    keys.Add("unknown");
                    break;
            }
            keys.RemoveAll(k => !HasPhrase(k));  // e.g. no recorded name for a sixth robot
            return new TalkAnswer
            {
                Robot = f.Id, Intent = intent, Score = score, Keys = keys.ToArray(), Text = TextOf(keys),
                Stop = intent == "stop" && !f.Stopped,
            };
        }

        private static string Cause(RobotFacts f)
        {
            return f.Cause == "safety" ? "cause_safety" : f.Cause == "unsafe" ? "cause_unsafe" : "cause_estop";
        }

        private static void Job(RobotFacts f, List<string> keys)
        {
            if (f.Kind != "soup" && f.Kind != "food" && f.Kind != "dishes") { keys.Add(f.Stopped ? "no_job" : "idle"); return; }
            keys.Add("job_" + f.Kind);
            if (f.Table > 0) keys.Add("to_" + f.Table);
        }

        private static string Since(double seconds)
        {
            int[] marks = { 0, 5, 10, 20, 30, 45, 60, 120, 180, 300 };
            // Each mark covers the seconds closer to it than to its neighbours.
            int[] upTo = { 4, 8, 15, 25, 38, 53, 90, 150, 240 };
            for (int i = 0; i < upTo.Length; i++) if (seconds < upTo[i]) return "since_" + marks[i];
            return "since_300";
        }

        private static string Count(int n) { return n < 0 ? "n_0" : n <= 40 ? "n_" + n : "n_many"; }

        // ---- text similarity ----

        /// <summary>NFKC, katakana to hiragana, lower case, no spaces or punctuation.</summary>
        public static string Normalize(string text)
        {
            if (text == null) return "";
            var sb = new StringBuilder();
            foreach (char raw in text.Normalize(NormalizationForm.FormKC))
            {
                char c = char.ToLowerInvariant(raw);
                if (c >= 'ァ' && c <= 'ヶ') c = (char)(c - 0x60);
                if (char.IsWhiteSpace(c) || "。、，．,.!?！？「」『』（）()…・~〜".IndexOf(c) >= 0) continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static bool IsLatin(char c) { return c >= 'a' && c <= 'z'; }

        /// <summary>Drops the robot's name, so "ロボットBは今の状況を教えて" compares like "今の状況を教えて".</summary>
        private static string WithoutName(string text)
        {
            for (int i = 0; i < Letters.Length; i++)
                foreach (string name in new[] { "ろぼっと" + Letters[i], "ろぼっと" + Readings[i], Letters[i], Readings[i] })
                    foreach (string particle in new[] { "は", "の", "に", "さん", "くん", "ちゃん", "って", "" })
                        if (text.StartsWith(name + particle) && (particle.Length > 0 || name.StartsWith("ろぼっと")))
                            return text.Substring(name.Length + particle.Length);
            return text.StartsWith("ろぼっと") ? text.Substring(4) : text;
        }

        private static double Dice(string a, string b)
        {
            if (a.Length < 2 || b.Length < 2) return a == b ? 1 : 0;
            var counts = new Dictionary<string, int>();
            for (int i = 0; i + 1 < a.Length; i++) { string g = a.Substring(i, 2); int n; counts.TryGetValue(g, out n); counts[g] = n + 1; }
            int common = 0;
            for (int i = 0; i + 1 < b.Length; i++)
            {
                string g = b.Substring(i, 2); int n;
                if (counts.TryGetValue(g, out n) && n > 0) { common++; counts[g] = n - 1; }
            }
            return 2.0 * common / (a.Length - 1 + b.Length - 1);
        }
    }
}
