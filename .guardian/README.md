# Local Guardian settings

Place private operational settings at:

`guardian-settings.private.json`

The JSON file is ignored by Git. Start from
`../Assets/StreamingAssets/Guardian/guardian-settings.public.json`,
then replace its illustrative values locally. Do not copy operational values into
issues, logs, screenshots, build artifacts, or the public sample.

The local file is loaded automatically in the Unity Editor. A player build should
receive an external path with `GUARDIAN_SETTINGS_PATH` or
`-guardianSettingsPath <path>`; the private file is not included in the build.
