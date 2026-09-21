using AspireUI.Server.Models;

namespace AspireUI.Server.Services;

/// <summary>
/// An Android app as a stack: an emulator with a noVNC web view, and a small sidecar that waits for
/// the device to finish booting and then installs the APK over adb. The APK is a parameter, so the
/// same template serves whichever app is wanted without editing anything.
/// </summary>
/// <remarks>
/// This is the same shape as Nextended.Aspire.Hosting.Apk builds, written out as plain container
/// nodes so it works before that package is published, and so the stack stays editable here.
/// </remarks>
public static class ApkTemplate
{
    public const string Id = "android-apk";

    /// <summary>The emulator image, which bundles the Android emulator and the noVNC web view.</summary>
    private const string EmulatorImage = "budtmo/docker-android:emulator_11.0";

    /// <summary>adb comes from Alpine's android-tools; nothing else is needed to install an APK.</summary>
    private const string InstallerImage = "alpine:3.20";

    private const int WebPort = 6080;
    private const int AdbPort = 5555;

    /// <summary>
    /// The sample: VLC. Freely downloadable, plainly does something once it is up, and it makes the
    /// one real limit of this setup visible straight away - the picture arrives over noVNC, the
    /// sound does not, because VNC has no audio channel. VideoLAN publishes one APK per
    /// architecture rather than a universal one, so this is the x86_64 build: the emulator is
    /// x86_64, and the arm64 build would install here and then crash.
    /// </summary>
    public const string SampleApk = "https://get.videolan.org/vlc-android/3.7.1/VLC-Android-3.7.1-x86_64.apk";

    public static StackModel Create(string stackId, string? apkUrl = null)
    {
        var device = new NodeModel(Nid(), "device", "AddContainer", "device",
        [
            new WithCall("WithHttpEndpoint", [$"targetPort: {WebPort}"]),
            new WithCall("WithEndpoint", [$"targetPort: {AdbPort}", "name: \"adb\"", "scheme: \"tcp\""]),
            // Without this the device runs with no way to look at it.
            new WithCall("WithEnvironment", ["\"WEB_VNC\"", "\"true\""]),
            new WithCall("WithEnvironment", ["\"EMULATOR_DEVICE\"", "\"Samsung Galaxy S10\""]),
            // The emulator needs hardware virtualisation and will not run without it: it exits with
            // "RuntimeError: /dev/kvm cannot be found!" while the container keeps serving its
            // landing page. Docker refuses to create a container for a device the host does not
            // have, so on a host without /dev/kvm this line has to go - and then nothing works.
            new WithCall("WithContainerRuntimeArgs", ["\"--device\"", "\"/dev/kvm\""]),
        ], 120, 120, [Quote(EmulatorImage)]);

        var apk = new NodeModel(Nid(), "apkUrl", "AddParameter", "apk-url",
            [], 120, 420, [Quote(apkUrl ?? SampleApk), "true", "false"]);

        var installer = new NodeModel(Nid(), "installer", "AddContainer", "installer",
        [
            new WithCall("WithEntrypoint", ["\"/bin/sh\""]),
            new WithCall("WithEnvironment", ["\"APK_EMULATOR\"", "\"device\""]),
            new WithCall("WithEnvironment", ["\"APK_ADB_PORT\"", $"\"{AdbPort}\""]),
            new WithCall("WithEnvironment", ["\"APK_URLS\"", "apkUrl"]),
            new WithCall("WithArgs", ["\"-c\"", Quote(Script)]),
        ], 520, 120, [Quote(InstallerImage)]);

        var wait = new EdgeModel(Eid(), installer.Id, device.Id, "waitFor");

        return new StackModel(stackId, "Android app", "net10.0",
            [device, apk, installer], [wait], [], [], []);
    }

    /// <summary>
    /// What the sidecar runs. The order is the whole point: installing before the device reports
    /// sys.boot_completed reports success and silently installs nothing.
    /// </summary>
    private const string Script = """
        set -eu
        apk add --no-cache android-tools curl >/dev/null
        mkdir -p /apks
        n=0
        for u in $APK_URLS; do
          n=$((n+1))
          echo "[apk] downloading $u"
          curl -fsSL --retry 3 -o "/apks/download-$n.apk" "$u"
        done
        target="$APK_EMULATOR:$APK_ADB_PORT"
        echo "[apk] waiting for $target to accept adb and finish booting"
        waited=0
        while [ "$(adb connect "$target" 2>/dev/null | grep -c 'connected to')" = "0" ] ||
              [ "$(adb shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" != "1" ]; do
          waited=$((waited+5))
          if [ "$waited" -ge 900 ]; then
            echo "[apk] $target did not finish booting within 900s - the emulator needs /dev/kvm"
            exit 1
          fi
          sleep 5
        done
        echo "[apk] device is up after $waited seconds"
        adb shell pm list packages -3 | tr -d '\r' | sort > /tmp/before
        for f in /apks/*.apk; do
          echo "[apk] installing $f"
          adb install -r -g "$f" || adb install -r "$f"
        done
        adb shell pm list packages -3 | tr -d '\r' | sort > /tmp/after
        for p in $(comm -13 /tmp/before /tmp/after | sed 's/^package://'); do
          echo "[apk] starting $p"
          adb shell monkey -p "$p" -c android.intent.category.LAUNCHER 1 >/dev/null 2>&1 || true
        done
        echo "[apk] done"
        """;

    private static string Nid() => "n" + Guid.NewGuid().ToString("n")[..8];
    private static string Eid() => "e" + Guid.NewGuid().ToString("n")[..8];

    private static string Quote(string s) => ComposeImporter.Quote(s);
}
