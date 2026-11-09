# GPU Watchdog: how to use it

## 1. Start it
Double-click **GPU Watchdog** on your Desktop, then click **Yes** when Windows asks for permission.
The control panel opens. A small square with your GPU temperature appears in the tray, next to the clock.

- If you close the panel with **X**, the watchdog keeps running in the tray, so the website, the schedules and the safety stop keep working.
- To open the panel again, double-click the tray square, or double-click the Desktop shortcut again.
- To stop the watchdog completely, right-click the tray square and choose **Quit**. This also turns off remote access and the safety stop.

## 2. The website
*(We'll add the link once setup is finished.)* Bookmark it on your phone, or use **Share → Add to Home Screen** so it opens like an app.

It works from anywhere: mobile data, other Wi-Fi networks, other countries. Your PC makes one outgoing connection to a free Cloudflare relay, so nothing on your router has to be opened.

## 3. What the buttons do
| Button | What happens |
|---|---|
| **Chop Now** | Starts Salad if it's closed, then starts chopping straight away (it doesn't wait for the PC to be idle). It also clears a "hold". |
| **Pause** | Stops chopping. Salad stays open. Sets a hold. |
| **Close Salad** | Closes Salad completely. Sets a hold. |
| **Game Mode** | When a game starts, chopping pauses. About 10 seconds after the last game closes, Chop Now starts again. Steam games and the games in D:\Games are found automatically. |
| **Schedule off** | Turns the PC off, or closes Salad, at a time you pick. **7:00 AM** is pre-filled. Schedules are kept on the PC, so they still run if your phone is off. |
| **PC off now** | Windows shuts down after a 30-second countdown that you can cancel. **Once the PC is off, it can't be turned back on from the website.** |
| **Lights & Screen Off** *(PC panel only)* | Turns the SignalRGB lights off right away, then the monitors 20 seconds later. The PC keeps running and earning. Move the mouse to wake the screen. |

**What a "hold" means:** after you pause or close Salad yourself, Game Mode won't restart it. Press **Chop Now** (or **Clear hold** in the panel) when you want it back.

## 4. Safety stop
If the GPU reaches **85 °C** for 3 seconds in a row, Salad is closed and **locked out**:

- Nothing can restart it, not even the website.
- The lockout stays on after a reboot.
- If Salad starts again by itself, it is closed again.

Check the fans, dust and airflow first. Then, **on the PC**, press "I checked the PC, allow Salad again". You can change the limit only in the PC panel, never from the website.

## 5. Change the website password
Go to the PC panel → **Safety & Settings** → **Website password**. Type the new password twice and press **Save & sync**. The old password stops working right away, and anyone who was signed in gets signed out.

## 6. If the website says "Offline"
1. Check that the PC is on and the tray square is there. If it isn't, double-click the Desktop shortcut.
2. Check that the PC has internet. The watchdog reconnects by itself within a minute.

## 7. Updating
Just ask Claude to update it. Everything lives in **Desktop\GPU-Watchdog** and on GitHub.

## 8. Honest notes on security
- **Anyone with the website link can see the stats and earnings.** You can hide the earnings in the panel (General → Show earnings on the website), or require the password just to view.
- Every button on the website needs the password. After 5 wrong tries, that person is locked out for longer and longer: 1 minute, 5 minutes, 30 minutes, then 24 hours.
- A 6-digit code can still be guessed by someone determined. A longer password is safer, and you can change it any time.
- The watchdog never touches Salad's login, payments, files or network traffic. It only opens and closes Salad, and presses its Start and Stop buttons the way the Salad app does.
