-- GWS Updater: the small helper app behind "Check for Updates..." in GWS Business Suite.
-- The installed app runs in the App Sandbox, so it can't read this repo, build, or write to
-- /Applications - but it can open a URL. It opens gws-update://check, and this helper (outside
-- the sandbox) runs scripts/install-gws-mac.sh. install-gws-mac.sh compiles this file into
-- ~/Library/Application Support/GWS Business Suite/GWS Updater.app, filling in the paths below.

property installScript : "__INSTALL_SCRIPT__"
property logFile : "__LOG_FILE__"

on open location theURL
	if theURL starts with "gws-update://update" then
		startUpdate()
	else
		checkForUpdates()
	end if
end open location

on run
	checkForUpdates()
end run

on checkForUpdates()
	try
		do shell script "test -f " & quoted form of installScript
	on error
		tell me to activate
		display dialog "The source folder GWS Business Suite was installed from isn't on this Mac any more:" & return & return & installScript buttons {"OK"} default button 1 with title "Check for Updates" with icon caution
		return
	end try
	try
		do shell script "/bin/bash " & quoted form of installScript & " --check"
		tell me to activate
		display dialog "GWS Business Suite is up to date." buttons {"OK"} default button 1 with title "Check for Updates" with icon note
	on error errText number errNum
		tell me to activate
		if errNum is 10 then
			set choice to button returned of (display dialog "A newer version is in your source folder. Install it now? The app closes, rebuilds (usually a minute or two) and opens again by itself." buttons {"Later", "Install and Reopen"} default button 2 with title "Update Available" with icon note)
			if choice is "Install and Reopen" then startUpdate()
		else
			display dialog "Couldn't check for updates: " & errText buttons {"OK"} default button 1 with title "Check for Updates" with icon caution
		end if
	end try
end checkForUpdates

-- Runs detached: the install script quits the app, rebuilds, reinstalls and reopens it.
on startUpdate()
	do shell script "mkdir -p \"$(dirname " & quoted form of logFile & ")\" && nohup /bin/bash " & quoted form of installScript & " --relaunch > " & quoted form of logFile & " 2>&1 < /dev/null &"
end startUpdate
