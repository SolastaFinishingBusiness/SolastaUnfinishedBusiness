$target = ".\obj\Release Install\SolastaUnfinishedBusiness"
if (!(Test-Path $target)) {mkdir $target}
# Write-Host "BlenderModels"
# if (!(Test-Path $target\BlenderModels)) {Copy-Item -Path "$env:SolastaInstallDir\Mods\SolastaUnfinishedBusiness\BlenderModels" -Destination $target -Recurse}
# Write-Host "Portraits"
# if (!(Test-Path $target\Portraits)) {Copy-Item "$env:SolastaInstallDir\Mods\SolastaUnfinishedBusiness\Portraits" $target -Recurse}
# Write-Host "Settings"
# if (!(Test-Path $target\Settings)) {Copy-Item "$env:SolastaInstallDir\Mods\SolastaUnfinishedBusiness\Settings" $target -Recurse}
# Write-Host "UnofficialTranslations"
# if (!(Test-Path $target\UnofficialTranslations)) {Copy-Item "$env:SolastaInstallDir\Mods\SolastaUnfinishedBusiness\UnofficialTranslations" $target -Recurse}
# Compress-Archive -Path "$env:SolastaInstallDir\Mods\SolastaUnfinishedBusiness\UnofficialTranslations" -Update -DestinationPath ".\SolastaUnfinishedBusiness.zip"

if (Test-Path -Path ".\obj\Release Install\SolastaUnfinishedBusiness.dll") {
	# the changelog goes in the mod's Documentation folder; a staging folder from an older build may still hold the .txt
	if (Test-Path "$target\ChangelogHistory.txt") {Remove-Item "$target\ChangelogHistory.txt"}
	if (!(Test-Path "$target\Documentation")) {mkdir "$target\Documentation"}
	Copy-Item "..\Documentation\ChangelogHistory.md" "$target\Documentation" -Force
    Copy-Item $target\..\SolastaUnfinishedBusiness.dll $target -Force
	Copy-Item $target\..\SolastaUnfinishedBusiness.pdb $target -Force
    Copy-Item ".\Info.json" $target -Force
    #Copy-Item "..\lib\NAudio.dll" $target
 }

Write-Host "Finishing up"
Compress-Archive $target $target\..\SolastaUnfinishedBusiness.zip 