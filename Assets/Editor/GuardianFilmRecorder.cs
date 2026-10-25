using System;
using System.Globalization;
using System.IO;
using Unity.Collections;
using UnityEditor;
using UnityEditor.Media;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Guardian.UnitySim.Editor
{
    /// <summary>
    /// Films the demo: opens the restaurant, sets the Game view to 1920x1080, presses Play and lets
    /// GuardianAutoFilm record Recordings/robomori_demo_*.mp4 (H.264 and AAC, with Unity's own encoder:
    /// nothing else to install). Two films: the 30-second main film (the agreed demo) and the 40-second
    /// presentation-mode film. Three ways in, all explicit:
    /// - the menu "Robomori/30秒の動画を撮る（本編）" or "Robomori/40秒の動画を撮る（発表モード）";
    /// - scripts/film-demo.cmd with Unity closed: it starts Unity with -executeMethod ...Launch, and Unity
    ///   closes again when the film is written;
    /// - scripts/film-demo.cmd with this project already open: it leaves a request in Temp/robomori-film.request,
    ///   which this editor picks up within a second (stopping Play first if it is running).
    /// The load hook re-attaches the encoder after each script reload and passes a film that was asked for on to
    /// Play; it starts nothing unless one of the above asked.
    /// </summary>
    [InitializeOnLoad]
    public static class GuardianFilmRecorder
    {
        private const string Pending = "Robomori.FilmPending", Reveal = "Robomori.FilmReveal", StoryKey = "Robomori.FilmStory";
        private const string Scene = "Assets/Scenes/GuardianRestaurant.unity";
        private static double nextPoll;

        private static string Request => Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Temp", "robomori-film.request");

        static GuardianFilmRecorder()
        {
            GuardianAutoFilm.Sink = new Mp4Sink();
            GuardianAutoFilm.Finished -= OnFinished;
            GuardianAutoFilm.Finished += OnFinished;
            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
            // Pressing Play reloads the scripts: hand the request over to the run that is starting.
            if (SessionState.GetBool(Pending, false) && EditorApplication.isPlayingOrWillChangePlaymode)
            {
                SessionState.EraseBool(Pending);
                GuardianAutoFilm.Requested = true;
                GuardianAutoFilm.RequestedStory = (GuardianFilmStory)SessionState.GetInt(StoryKey, 0);
            }
        }

        [MenuItem("Robomori/30秒の動画を撮る（本編）")]
        public static void FilmMain() => Film(GuardianFilmStory.Main);

        [MenuItem("Robomori/40秒の動画を撮る（発表モード）")]
        public static void FilmPitch() => Film(GuardianFilmStory.Pitch);

        private static void Film(GuardianFilmStory story)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            { EditorUtility.DisplayDialog("Robomori", "再生（▶）を止めてから、もう一度選んでください。", "OK"); return; }
            if (EditorSceneManager.GetActiveScene().path != Scene && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            SessionState.SetBool(Reveal, true);
            Start(story);
        }

        /// <summary>scripts/film-demo.cmd with Unity closed: -executeMethod Guardian.UnitySim.Editor.GuardianFilmRecorder.Launch -guardianLaunchRequested</summary>
        public static void Launch()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-guardianLaunchRequested") < 0) throw new InvalidOperationException("Explicit launch required");
            if (EditorSceneManager.GetActiveScene().isDirty) throw new InvalidOperationException("Unsaved scene; refusing to replace it");
            Start(GuardianAutoFilm.StoryFromArguments());
        }

        /// <summary>scripts/film-demo.cmd with this project open: a request file, at most 10 minutes old.</summary>
        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < nextPoll) return;
            nextPoll = EditorApplication.timeSinceStartup + 1;
            string request = Request;
            if (!File.Exists(request) || GuardianAutoFilm.Busy || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorApplication.isPlaying) { EditorApplication.isPlaying = false; return; }  // the film starts from a fresh Play
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            string[] asked;
            try { asked = File.ReadAllText(request).Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries); File.Delete(request); }
            catch (Exception e) { Debug.LogWarning("[Robomori] Film request unreadable: " + e.Message); return; }
            // "<UTC time> [Main|Pitch]"
            if (asked.Length == 0 || !DateTime.TryParse(asked[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when) || (DateTime.UtcNow - when.ToUniversalTime()).TotalMinutes > 10)
            { Debug.Log("[Robomori] Ignored an old film request"); return; }
            var story = asked.Length > 1 && asked[1].Equals("Pitch", StringComparison.OrdinalIgnoreCase) ? GuardianFilmStory.Pitch : GuardianFilmStory.Main;
            var active = EditorSceneManager.GetActiveScene();
            if (active.path != Scene && active.isDirty)
            { GuardianAutoFilm.Status("error 保存していないシーン（" + active.name + "）があります。保存してから、もう一度実行してください"); return; }
            GuardianAutoFilm.Status("accepted");
            Start(story);
        }

        private static void Start(GuardianFilmStory story)
        {
            if (EditorSceneManager.GetActiveScene().path != Scene)
            {
                if (File.Exists(Scene)) EditorSceneManager.OpenScene(Scene);
                else { EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); new GameObject("Guardian explicit demo").AddComponent<GuardianSimulator>().EnsureWorld(); }
            }
            SessionState.SetBool(Pending, true);
            SessionState.SetInt(StoryKey, (int)story);
            GuardianAutoFilm.Requested = true;  // for projects that enter Play without reloading scripts
            GuardianAutoFilm.RequestedStory = story;
            try
            {
                PlayModeWindow.SetViewType(PlayModeWindow.PlayModeViewTypes.GameView);
                PlayModeWindow.SetCustomRenderingResolution(1920, 1080, "Robomori 1080p");
            }
            catch (Exception e) { Debug.LogWarning("[Robomori] Game view stays at its current size: " + e.Message); }
            Debug.Log("[Robomori] Filming the " + (story == GuardianFilmStory.Main ? "30-second main film" : "40-second presentation-mode film") + ": the restaurant opens for about 10 seconds first");
            EditorApplication.isPlaying = true;
        }

        private static void OnFinished(string output)
        {
            EditorApplication.delayCall += () =>
            {
                EditorApplication.isPlaying = false;
                Debug.Log("[Robomori] 動画を保存しました: " + output);
                if (SessionState.GetBool(Reveal, false))
                {
                    SessionState.EraseBool(Reveal);
                    if (File.Exists(output) || Directory.Exists(output)) EditorUtility.RevealInFinder(output);
                }
                // Started by film-demo.cmd with Unity closed: close it again once Play has stopped.
                if (Array.IndexOf(Environment.GetCommandLineArgs(), "-executeMethod") >= 0
                    && Array.IndexOf(Environment.GetCommandLineArgs(), "Guardian.UnitySim.Editor.GuardianFilmRecorder.Launch") >= 0)
                    EditorApplication.delayCall += () => EditorApplication.Exit(0);
            };
        }

        private sealed class Mp4Sink : IGuardianFilmSink
        {
            private MediaEncoder encoder;

            public string Begin(string path, int width, int height, int fps, int sampleRate)
            {
                var video = new VideoTrackAttributes
                { frameRate = new MediaRational(fps), width = (uint)width, height = (uint)height, includeAlpha = false, bitRateMode = VideoBitrateMode.High };
                string file = path + ".mp4";
                encoder = sampleRate > 0
                    ? new MediaEncoder(file, video, new AudioTrackAttributes { sampleRate = new MediaRational(sampleRate), channelCount = 2, language = "ja" })
                    : new MediaEncoder(file, video);
                return file;
            }

            public void Frame(Texture2D frame) => encoder.AddFrame(frame);
            public void Sound(NativeArray<float> stereo) => encoder.AddSamples(stereo);
            public void End() { encoder?.Dispose(); encoder = null; }
        }
    }
}
