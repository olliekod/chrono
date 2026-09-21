using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;

namespace ChronoRecorder
{
    /// <summary>A sound device as Windows lists it in Settings > Sound.</summary>
    public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

    /// <summary>Which device to use: the one asked for if it is still there, otherwise Windows' default. Pure and tested.</summary>
    public static class AudioDevicePicker
    {
        /// <param name="wantedId">The saved choice; empty or null means "whatever Windows uses".</param>
        /// <returns>The device, and whether the wanted one was missing so the default was used instead.</returns>
        public static (AudioDeviceInfo? Device, bool FellBack) Choose(IReadOnlyList<AudioDeviceInfo> available, string? wantedId)
        {
            var fallback = available.FirstOrDefault(d => d.IsDefault) ?? available.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(wantedId)) return (fallback, false);

            var wanted = available.FirstOrDefault(d => d.Id.Equals(wantedId, StringComparison.OrdinalIgnoreCase));
            return wanted != null ? (wanted, false) : (fallback, true);
        }
    }

    /// <summary>The sound devices on this PC (through Windows' Core Audio, like the Sound control panel).</summary>
    public static class AudioDevices
    {
        /// <summary>Speakers/headphones (what the game plays to) or microphones, active ones only, default marked.</summary>
        public static List<AudioDeviceInfo> List(DataFlow flow)
        {
            var result = new List<AudioDeviceInfo>();
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                string? defaultId = null;
                try { defaultId = enumerator.GetDefaultAudioEndpoint(flow, Role.Console).ID; } catch { /* no default device */ }

                foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                {
                    result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId));
                    device.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Couldn't list sound devices: {ex.Message}");
            }
            return result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Open the wanted device, or the default one if it isn't available. Null when there is no device at all.</summary>
        public static MMDevice? Open(DataFlow flow, string? wantedId, out bool fellBack)
        {
            fellBack = false;
            var available = List(flow);
            var (choice, missing) = AudioDevicePicker.Choose(available, wantedId);
            fellBack = missing;
            if (choice == null) return null;

            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDevice(choice.Id);
        }
    }
}
