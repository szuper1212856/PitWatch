# How to ship an update

Once someone has installed PitWatch, they never download it again - the app updates itself.
Your job is just to publish releases correctly.

## Publishing a new version

1. Update the version in `PitWatch.Core/AppInfo.cs` and `PitWatch.Gui/PitWatch.Gui.csproj`.
   Keep them the same.
1. Rewrite `RELEASE_NOTES.md` to describe what changed in this version. This is what
   users see when they click "What's new?" on the update banner, so write it for them,
   not for yourself - what's different, in plain language. Markdown headings and bullet
   lists both work.
2. Run:
   ```
   build-release.bat 1.1.0
   ```
3. Create a GitHub Release tagged `v1.1.0`.
4. **Upload everything in the `Releases` folder**, not just the Setup.exe.

That last point matters. The `.nupkg` and `RELEASES` files are what installed copies check
to discover and download the update. If you upload only `Setup.exe`, new users can install
fine but nobody already running PitWatch will ever be told there's an update.

## What users experience

- On launch, PitWatch quietly checks GitHub in the background.
- If there's a newer version, it downloads it while they carry on using the app.
- A green banner appears: "PitWatch 1.1.0 is ready to install."
- They click **Restart & update** and they're on the new version in a couple of seconds.
- If they click **Later**, it's applied next time they open PitWatch anyway.

Only the changed parts get downloaded, not the whole app, so updates are small.

## Before your first public release

Set the repository URL in `PitWatch.Gui/UpdateService.cs`:

```csharp
private const string ReleaseUrl = "https://github.com/YOURNAME/PitWatch";
```

It currently points at `szuper1212856/PitWatch` - change it if that's not where you publish.

## Test the update path before anyone else uses it

This is the one thing worth being careful about, because a broken updater is much worse
than no updater.

1. Build and install `1.0.0`, run it, change a setting.
2. Publish `1.0.1` as a GitHub Release.
3. Open your installed `1.0.0` and confirm the banner appears.
4. Click **Restart & update**.
5. Confirm it comes back as `1.0.1` **and your setting is still there**.

Step 5 is the important one - it proves user data survives updates.

## User data location

Settings, saved sessions and custom callouts live in:

```
%APPDATA%\PitWatch\
```

Deliberately outside the program folder, so updates and reinstalls never touch them.
Anything from an older install that kept data next to the .exe is migrated automatically
on first run.

## Version numbers

Velopack requires versions to go up, and won't let a user "update" to an older build. Use
plain semantic versions: `1.0.0`, `1.0.1`, `1.1.0`.

## The dev launcher

`PitWatch.DevLauncher` is a developer-only tool - build info, a live log console, and
launch options for testing (force developer mode, reset config to re-run the first-run
wizard, clear the log).

It is a separate executable and is never published: `build-release.bat` only publishes
`PitWatch.Gui`, and it aborts if the launcher somehow appears in the publish folder. Users
cannot see or reach it.

**Double-click `dev.bat`** in the solution folder. It builds and opens the launcher - no
terminal commands needed. (`dotnet run --project PitWatch.DevLauncher -c Debug` also works
if you prefer the command line.)
