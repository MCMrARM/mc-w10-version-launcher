# MCLauncher

This tool allows you to install several versions of Minecraft: Windows 10 Edition (Bedrock) side-by-side.
This is useful if you want to test beta versions, releases or anything else side-by-side without needing to uninstall and reinstall the game.

## Disclaimer
This tool will **not** help you to pirate the game; it requires that you have a Microsoft account which can be used to download Minecraft from the Store.

## Prerequisites
- A Microsoft account connected to Microsoft Store which **owns Minecraft for Windows 10**
- **Administrator permissions** on your user account (or access to an account that has)
- **Developer mode** enabled for app installation in Windows 10 Settings
- If you want to be able to use beta versions, you'll additionally need to **subscribe to the Minecraft Beta program using Xbox Insider Hub**.
- [Microsoft Visual C++ Redistributable](https://aka.ms/vs/16/release/vc_redist.x64.exe) installed.

## Setup
- Download the latest release from the [Releases](https://github.com/MCMrARM/mc-w10-version-launcher/releases) section. Unzip it somewhere.
- Run `MCLauncher.exe` to start the launcher.

## Common Pitfalls and Issues
Unfortunately the launcher has become quite unreliable with the migration to GDK.
Combined with Windows 11's steady worsening in quality and stability, most of these issues seem to be caused by Windows itself, while others are caused by the method the launcher uses to extract the game files.

As I (@dktapps) don't use Windows as my primary OS anymore, and find Windows very tiresome to deal with, it's not likely these will get significantly improved, so here are some known issues and workarounds.

### `Invalid argument` error when launching the game via the start menu
Move the launcher to a shorter path, and launch it at least one time from inside the launcher after moving it.

This seems to be a result of the path to `Minecraft.Windows.exe` being too long somehow, and the issue seems to occur on newer versions of Windows even if MAX_PATH is increased.
Older versions didn't have this issue, so it's not clear what caused the issue.

### Failed to decrypt Minecraft.Windows.exe
- Make sure you installed Minecraft (or Minecraft Preview) from the Store before using the launcher
- A valid license is required, you can't pirate the game using the launcher
- Sometimes using `Tools -> Cleanup for installing Minecraft from Microsoft Store` may help resolve issues
- Try rebooting the machine if all else fails. Sometimes Windows's services get glitchy for no obvious reason and cause issues that a reboot will sometimes clear out

### Invalid 16-bit application or SmartScreen saying "this app can't run on your PC"
This is usually a failure to get a decrypted version of the Minecraft exe. Try the steps above first, but also check `File -> Open log file` and see if any errors are present

### `minecraft://` and similar deeplinks not working
Check keys like `HKEY_CLASSES_ROOT\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\PackageRepository\Extensions\windows.protocol\minecraft` and similar keys for duplicate entries.

Sometimes the registry ends up with duplicate entries for these protocols for reasons that aren't yet clear.

Deleting the dead entries may help fix deeplinks.

## Compiling the launcher yourself
You'll need Visual Studio with Windows 10 SDK version 10.0.17763 and .NET Framework 4.6.1 SDK installed. You can find these in the Visual Studio Installer if you don't have them out of the box.
The project should build out of the box with VS as long as you haven't done anything bizarre.

## Frequently Asked Questions
**Does this allow running multiple instances of Minecraft: Bedrock at the same time?**

At the time of writing, no. It allows you to _install_ multiple versions, but only one version can run at a time.
