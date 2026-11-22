@echo off
echo Packaging the release to SolastaUnfinishedBusiness.zip
rem powershell -ExecutionPolicy Bypass -File ".\ZipRelease.ps1"
echo Copy release info.
copy Info.json "obj\Release Install\SolastaUnfinishedBusiness" /y
rem the changelog goes in the mod's Documentation folder; a staging folder from an older build may still hold the .txt
if exist "obj\Release Install\SolastaUnfinishedBusiness\ChangelogHistory.txt" del "obj\Release Install\SolastaUnfinishedBusiness\ChangelogHistory.txt"
if not exist "obj\Release Install\SolastaUnfinishedBusiness\Documentation" mkdir "obj\Release Install\SolastaUnfinishedBusiness\Documentation"
copy ..\Documentation\ChangelogHistory.md "obj\Release Install\SolastaUnfinishedBusiness\Documentation" /y
cd "obj\Release Install"
echo Copy mod files
copy SolastaUnfinishedBusiness.dll SolastaUnfinishedBusiness /y
copy SolastaUnfinishedBusiness.pdb SolastaUnfinishedBusiness /y
echo Zip release
tar -acf "SolastaUnfinishedBusiness.zip" SolastaUnfinishedBusiness