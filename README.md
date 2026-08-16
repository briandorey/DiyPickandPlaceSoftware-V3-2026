# DIY Pick and Place V3 2026

This is almost a complete rewrite of the code for the software for our DIY Pick and Place
machine which runs from a KFlop motion controller and a custom USB interface.

You can view more details about the project on https://www.briandorey.com/category/diy-pick-and-place

This version uses Windows Presentation Foundation and ASP.Net 10 and the old video system has been replaced with OpenCvSharp4.

KMotion5.4.5 is required for communication with the controller this can be downloaded and installed from https://www.store.dynomotion.com/pages/download

Some of the conversion from the legacy version was assisted by Claude Code AI to find replacements and upgraded libaries to use in the new frameworks.

## Stream Deck / global hotkeys

The app registers global hotkeys (Windows `RegisterHotKey`, handled in
[MainWindow.xaml.cs](MainWindow.xaml.cs)) on the F13–F18 keys. These have no
physical key on a standard keyboard, so they won't collide with any other
shortcut on the PC. In the Elgato Stream Deck app, assign each button a
**System → Hotkey** action set to the corresponding key below - no plugin
required.

| Stream Deck button | Key | Action | Works from any page? |
|---|---|---|---|
| 1 | F13 | Start | No - requires the "Build PCB" page active |
| 2 | F14 | Home | Yes |
| 3 | F15 | E-Stop | Yes |
| 4 | F16 | Chip Feeder | No - requires the "Build PCB" page active |
| 5 | F17 | Uncheck All | No - requires the "Build PCB" page active |
| 6 | F18 | Check All | No - requires the "Build PCB" page active |

Home and E-Stop are handled directly by `MainWindow` and don't depend on
which page is showing. Start, Chip Feeder, Check All, and Uncheck All operate
on state (loaded board, feeder number) that only exists on the "Build PCB"
page - if that page isn't active when one of those hotkeys fires, the app
shows a status warning instead of doing nothing silently.


Deploy using

```
dotnet publish PickandPlace2026.csproj -p:PublishProfile=win-x64.pubxml -c Release
```

Remove extra unused language files in powershell

```
Get-ChildItem "C:\PickAndPlace" -Directory | Where-Object {
    $_.Name -ne 'en-GB' -and (Test-Path (Join-Path $_.FullName 'Microsoft.ui.xaml.dll.mui'))
} | Remove-Item -Recurse -Force
```

## USBGenericHID folder

This folder contains an upgraded USB library from https://www.waitingforfriday.com/?p=415 which has been changed to remove the Windows.Forms requirement and is now 64 bit