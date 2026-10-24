# Local BOOTH Nekobot import (not distributed)

Model: https://booth.pm/ja/items/4699566 — Studio NKY. Third-party distribution permission has **not** been confirmed.

1. In Unity **6000.3.12f1**, import the original `Nekobot.unitypackage` with **Assets > Import Package > Custom Package...**.
2. Confirm `Assets/Nekobot/nekobot.fbx`, `nekobot.prefab`, `tex.png` and the original `.mat` files are present.
3. Open `Assets/Scenes/GuardianRestaurant.unity` and choose **Guardian > Replace Robot Visuals With Local Nekobot**. This avoids rebuilding the restaurant or overwriting your own scene edits.
4. Save the scene with Ctrl+S. For size/facing adjustments choose **Guardian > Nekobot Model Settings...**.
5. Existing model assets, ZIP packages and locally generated compatible materials remain excluded from Git.

The FBX is preferred because the avatar prefab references VRChat SDK components. The importer normalizes mesh height and floor alignment and creates local compatible Built-in/URP material copies if necessary.

Do not publish, redistribute or check in the original model without checking the author's terms and obtaining any necessary authorization.


### One-command WSL installation
Close the Unity Editor and run from the repository:

    bash scripts/apply-nekobot.sh --open

This copies the original BOOTH model only to your local Assets/Nekobot folder
and uses a Unity 6000.3.12f1 headless session to save the updated scene.
No original FBX, prefab, textures, Unity packages or model-based materials are
committed to Git. See the repository README for prerequisites and log location.
