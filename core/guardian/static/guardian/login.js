// ロボもり 中核の入口。PC 側（core\start.ps1・core\host\otp.ps1）が開いた「/login#code=XXXX-XXXX」なら、その合言葉で自動で入る。
// 合言葉はページのソースには入っていない。住所の # の後ろはサーバーにも送られず、読んだらすぐ住所からも履歴からも消す。
"use strict";
(() => {
  const match = /^#code=([A-Za-z2-9]{4}-?[A-Za-z2-9]{4})$/.exec(window.location.hash);
  if (!match) return;
  window.history.replaceState(null, "", window.location.pathname + window.location.search);
  const input = document.getElementById("code");
  const form = input && input.form;
  if (!form) return;
  input.value = match[1].toUpperCase();
  const note = document.getElementById("auto");
  if (note) note.hidden = false;
  if (form.requestSubmit) form.requestSubmit();
  else form.submit();
})();
