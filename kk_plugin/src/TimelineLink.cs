using System;
using System.Reflection;

namespace KK2Blender
{
    /// <summary>
    /// Reaches the Timeline plugin (Timeline.Timeline, static API) by reflection, so the export works,
    /// and builds, without Timeline installed. Only the members the bake needs.
    /// </summary>
    internal static class TimelineLink
    {
        private static bool tried;
        private static Type type;
        private static PropertyInfo playbackTime;
        private static PropertyInfo duration;
        private static PropertyInfo isPlaying;
        private static MethodInfo play;
        private static MethodInfo pause;
        private static MethodInfo stop;
        private static MethodInfo seek;
        private static MethodInfo estimateRealDuration;

        public static bool Available
        {
            get
            {
                Init();
                return type != null && playbackTime != null && duration != null && play != null;
            }
        }

        private static void Init()
        {
            if (tried)
            {
                return;
            }
            tried = true;
            try
            {
                type = Type.GetType("Timeline.Timeline,Timeline");
                if (type == null)
                {
                    return;
                }
                const BindingFlags s = BindingFlags.Static | BindingFlags.Public;
                playbackTime = type.GetProperty("playbackTime", s);
                duration = type.GetProperty("duration", s);
                isPlaying = type.GetProperty("isPlaying", s);
                play = type.GetMethod("Play", s, null, Type.EmptyTypes, null);
                pause = type.GetMethod("Pause", s, null, Type.EmptyTypes, null);
                stop = type.GetMethod("Stop", s, null, Type.EmptyTypes, null);
                seek = type.GetMethod("Seek", s, null, new[] { typeof(float) }, null);
                estimateRealDuration = type.GetMethod("EstimateRealDuration", s, null, Type.EmptyTypes, null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Timeline was found but couldn't be read: " + e.Message);
                type = null;
            }
        }

        public static float PlaybackTime { get { return (float)playbackTime.GetValue(null, null); } }

        public static float Duration { get { return (float)duration.GetValue(null, null); } }

        public static bool IsPlaying { get { return isPlaying != null && (bool)isPlaying.GetValue(null, null); } }

        /// <summary>
        /// The length of the animation the user actually made: the last keyframe when Timeline can tell
        /// (its EstimateRealDuration), otherwise the Timeline's full duration.
        /// </summary>
        public static float RealDuration
        {
            get
            {
                if (estimateRealDuration != null)
                {
                    try
                    {
                        float d = (float)estimateRealDuration.Invoke(null, null);
                        if (d > 0f)
                        {
                            return d;
                        }
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning("Timeline.EstimateRealDuration failed: " + e.Message);
                    }
                }
                return Duration;
            }
        }

        /// <summary>Play toggles in Timeline: it pauses when already playing.</summary>
        public static void Play()
        {
            if (!IsPlaying)
            {
                play.Invoke(null, null);
            }
        }

        public static void Pause()
        {
            if (pause != null)
            {
                pause.Invoke(null, null);
            }
            else if (IsPlaying)
            {
                play.Invoke(null, null);
            }
        }

        /// <summary>Back to 0 and paused.</summary>
        public static void Stop()
        {
            if (stop != null)
            {
                stop.Invoke(null, null);
            }
            else
            {
                Pause();
                Seek(0f);
            }
        }

        public static void Seek(float t)
        {
            if (seek != null)
            {
                seek.Invoke(null, new object[] { t });
            }
        }
    }
}
