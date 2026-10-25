# 事業の背景と展開

ロボもりを、配膳のデモから製造業の現場へどう広げるかをまとめました。数字はすべて公開されている資料からとり、出典を末尾に付けています。料金や収支の計画は、この資料には入れていません。

市場規模・料金・売上の試算まで入れたスライド版は [ロボもり｜補足資料](https://docs.google.com/presentation/d/1Fd_nJmBFcWzg-3xrGk1wrq9fqDtnbcEbSC7ygMG9M_Q/edit?usp=sharing)、Hack Day の発表は [ロボもり｜Hack Day 発表スライド](https://docs.google.com/presentation/d/1WQIn4H3_ua5t65tx0b36FbvmO08A6MnyDJwDx9BLvwA/edit?usp=sharing) です。

## 1. 背景：ロボットは増え、働き手は減り、止まると高くつく

| 数字 | 内容 |
|---|---|
| **500 万台** | 世界の工場で動いている産業用ロボットの公表台数。前年比 +9%で、7年前の2倍を超えた[^ifr] |
| **36,219 台** | 日本で新しく導入された産業用ロボットの公表台数（世界3位）。従業員1万人あたりのロボット台数は446台で世界4位[^ifr][^ifr-density] |
| **1,033 万人** | 製造業で働く人の公表人数。以前の公表値1,055万人から減っている[^mono1][^mono2026] |
| **−18.2** | 中小製造業の従業員の過不足を示す公表指数。マイナスは人手不足を表す[^mono1] |
| **3 割程度** | 従業員 50 人以下の企業のうち、ロボットを入れている割合。301 人以上の企業では 6 割を超える[^mono3] |
| **約 11%** | 世界の大企業 500 社が、計画外の停止で失っている売上の割合（年に約 1.4 兆ドル）[^siemens] |
| **230 万ドル** | 自動車工場が、ラインの停止1時間で失うとする公表額[^siemens] |

ロボットが増えるほど、1 台の不調や停止が現場全体に響きます。人手が足りない現場では、不調のたびに人が代わりに運んだり、判断を待ったりする余裕もありません。

## 2. なぜ今か：国の戦略と、海外の規制が動いた

- **国のAIロボティクス戦略**は、制度の課題として安全性の論証と責任の分け方を挙げています[^strategy]。
- **AIセーフティ・インスティテュート**（AISI）の「AIロボティクスに関するセーフティ評価観点ガイド」は、評価の観点を整理しており、事例の一つが飲食店の搬送ロボットです。ロボットが代わりの行動を選んだときは、選んだ基準と結果をログに残し、後から確かめられることを求めています[^aisi]。
- **つながるロボットは、攻撃の入口にもなります。** インターネットから直接たどれた産業用ロボットが見つかり[^rogue]、協働ロボットの安全設定を無効にしてプログラムを暗号化する攻撃も実証されています[^akerbeltz]。製造業は複数年にわたり、最も多く攻撃を受けた業種と報告されています[^xforce]。
- **EUの機械規則**は、つないだ機器から危険を生じさせないこと、安全に関わるソフトと設定を改ざんから守ることを求めています[^machinery]。産業用ロボットの安全規格 ISO 10218-1:2025 にも、サイバー脅威の評価が加わりました[^iso]。

## 3. 解く課題：劣化したロボットも動き続け、判断は現場に残る

- ロボットは、動いていても性能や品質が落ちていることがあります[^nist]。野外で使われた地上ロボットの平均故障間隔は 6〜20 時間という報告もあり、故障は珍しいことではありません[^ugv]。
- 異常に気づいたとき、今の現場の選択肢は「全部止める」か「そのまま続ける」です。全部止めれば、残っている能力で担えた仕事まで失います。そのまま続ければ、ロボットの状態に合わない仕事を任せ続けることになります。
- どの仕事を外して誰に回すかは、結局その場の人が判断しています。

## 4. ロボもりの位置づけ：今ある仕組みの上に載せる

ロボもりは、今ある運行管理や安全装置を置き換えません。それらの上に載せて、足りない部分だけを受け持ちます。ロボットの買い替えも要りません。

| 受け持つもの | 今ある仕組み | ロボもり |
|---|---|---|
| 運行・仕事の配分・交通の制御 | フリート管理（例：OMRON FLOW Core[^flow]） | 受け持たない。仕事の可否と割り当て直しの依頼を渡す |
| 安全への応答・非常停止 | 安全コントローラー（例：Pilz PNOZmulti 2[^pilz]） | 受け持たない。安全機能は置き換えない |
| その機体に任せてよい仕事・理由・人の確認 | — | **受け持つ** |
| 割り当て直しと、引き継ぎの記録 | — | **受け持つ** |

## 5. 誰が使い、どう広げるか

**使う人**

- 買う判断をするのは、製造の責任者です。見るのは、効果・予算・責任の分け方です。
- 日々使うのは、現場の担当者と設備保全の担当者です。
- ロボットの状態の取り込みや現場のルールの設定は、導入支援会社（SI）と組んで行います。

**広げる順番**

1. **配膳で試す**：このリポジトリのシミュレーターで、判断と引き継ぎを示しました。
2. **実機で確かめる**：Hack Day で実物のセンサーをつなぎ、実機での判断の確かさを確かめます。
3. **製造業の 1 工程で有償の評価（PoC）**：異常を再現し、記録を確かめ、評価報告を出します。最初の聞き取りで、現場で判断が必要になる頻度と、その負担を確かめます。
4. **拠点への導入**：今ある仕組みへの読み取りの接続と、現場のルールの設定をします。
5. **継続利用**：記録と引き継ぎの履歴を残し、設定を変えたときは評価し直します。

## 6. 効果の測り方

導入の前と後で、次の 3 つを比べます。

- **判断の時間**：異常に気づいてから、適切な対応が決まるまでの秒数
- **判断の正しさ**：誤った選択や、禁止された行動の数
- **人の負担**：調査と手配にかかった人手（人時）

測り方は、製造業で使われている方法に合わせます。

- **設備総合効率（OEE）**[^oee]：全部止めずに残った能力で動かせば、時間稼働率が上がります。精密な仕事だけを外せば、良品率を守れます。
- **リトルの法則（L ＝ λW）**[^little]：判断待ちの仕事の数 L は、判断が必要になる頻度 λ と、1 件あたりの判断の時間 W の積です。判断の時間が半分になれば、判断待ちの仕事も半分になります。
- **差の差**[^did]：導入した工程と、比べるための工程の変化の差を見て、生産量や季節による変動を取り除きます。

## 7. 関連する市場と、先行する事例

| 市場 | 規模 | 出典 |
|---|---|---|
| 予知保全 | 市場拡大が見込まれている | [^pdm] |
| 移動ロボット（AGV・AMR） | 市場拡大が見込まれている | [^amr] |

- **FANUC ZDT**：ロボットのデータから故障の予兆を見つけ、止まる前の保守につなげるサービス。35,000 台を超えるロボットをつなぎ、2,000 件を超える突発停止を防いだとメーカーは公表しています[^zdt]。ロボもりは、予兆が出た後も、その機体に任せる仕事を切り替えて運用を続けるところを受け持ちます。
- **BMW × Figure**：人型ロボットが、板金部品を取り出して所定の位置へ置く作業を実証しました。90,000個を超える部品を、約1,250稼働時間で扱ったと公表しています[^bmw]。人型ロボットが工場に入るほど、任せてよい仕事を状態に合わせて決める仕組みが要ります。

## 出典

[^ifr]: International Federation of Robotics, “[Five Million Robots now Operate in Factories Globally](https://ifr.org/ifr-press-releases/news/five-million-robots-now-operate-in-factories-globally),” press release.
[^ifr-density]: International Federation of Robotics, “[Robot Density Surges in Europe, Asia, and Americas](https://ifr.org/ifr-press-releases/news/robot-density-surges-in-europe-asia-and-americas).”
[^mono1]: 経済産業省ほか「[ものづくり白書 第1部第2章第1節](https://www.meti.go.jp/report/whitepaper/mono/2025/pdf/honbun_1_2_1.pdf)」。
[^mono2026]: 厚生労働省「[ものづくり白書 概要](https://www.mhlw.go.jp/content/11801500/001704747.pdf)」。
[^mono3]: 経済産業省ほか「[ものづくり白書 第1部第2章第3節](https://www.meti.go.jp/report/whitepaper/mono/2025/pdf/honbun_1_2_3.pdf)」。
[^siemens]: Siemens AG, “[The True Cost of Downtime](https://assets.new.siemens.com/siemens/assets/api/uuid:1b43afb5-2d07-47f7-9eb7-893fe7d0bc59/TCOD-2024_original.pdf).”
[^strategy]: AI ロボティクスに関する関係府省連絡会議「[AI ロボティクス戦略 概要](https://www8.cao.go.jp/cstp/ai/ai-semi_wg/2kai/siryo4_1.pdf)」「[AI ロボティクス戦略](https://www8.cao.go.jp/cstp/ai/ai-semi_wg/2kai/siryo4_2.pdf)」。
[^aisi]: AI セーフティ・インスティテュート「[AI ロボティクスに関するセーフティ評価観点ガイド 第1.0版](https://aisi.go.jp/assets/pdf/20260723_robotics_ai_safety_eval_v1.0_ja.pdf)」。
[^rogue]: F. Maggi ほか, “[Rogue Robots: Testing the Limits of an Industrial Robot's Security](https://documents.trendmicro.com/assets/wp/wp-industrial-robot-security.pdf),” Trend Micro / Politecnico di Milano.
[^akerbeltz]: V. Mayoral-Vilches ほか, “[Industrial robot ransomware: Akerbeltz](https://doi.org/10.48550/arXiv.1912.07714),” arXiv:1912.07714.
[^xforce]: IBM X-Force, “[X-Force Threat Intelligence Index](https://www.ibm.com/reports/threat-intelligence).”
[^machinery]: [Regulation (EU) on machinery](http://data.europa.eu/eli/reg/2023/1230/oj).
[^iso]: [ISO 10218-1](https://www.iso.org/standard/73933.html), Robotics — Safety requirements — Part 1: Industrial robots.
[^nist]: G. Qiao, B. A. Weiss, “[Quick Health Assessment for Industrial Robot Health Degradation and the Supporting Advanced Sensing Development](https://doi.org/10.1016/j.jmsy.2018.04.004),” J. Manuf. Syst.
[^ugv]: J. Carlson, R. R. Murphy, “[How UGVs Physically Fail in the Field](https://doi.org/10.1109/TRO.2004.838027),” IEEE Trans. Robot.
[^flow]: OMRON, “[OMRON announces FLOW Core 4.0 and Virtual Fleet Manager for scalable AMR deployment](https://industrial.omron.de/de/news-discover/news/omron-announces-flow-core-4.0-and-virtual-fleet-manager).”
[^pilz]: Pilz, “[PNOZmulti 2: Configurable safe small controllers](https://www.pilz.com/en-INT/products/small-controllers/configurable-control-systems).”
[^oee]: P. Muchiri, L. Pintelon, “[Performance measurement using overall equipment effectiveness (OEE)](https://doi.org/10.1080/00207540601142645),” Int. J. Prod. Res.
[^little]: J. D. C. Little, “[A Proof for the Queuing Formula: L = λW](https://doi.org/10.1287/opre.9.3.383),” Oper. Res.
[^did]: P. J. Gertler ほか, *[Impact Evaluation in Practice](https://doi.org/10.1596/978-1-4648-0779-4)*, 2nd ed., World Bank/IDB.
[^pdm]: Fortune Business Insights, “[Predictive Maintenance Market](https://www.fortunebusinessinsights.com/predictive-maintenance-market-102104).”
[^amr]: Interact Analysis, “[Mobile Robots Report](https://interactanalysis.com/wp-content/uploads/January-2026-Mobile-Robots-Report.pdf).”
[^zdt]: FANUC America, “[Zero Down Time (ZDT)](https://www.fanucamerica.com/docs/default-source/robotics-files/fanuc-zero-down-time-brochure.pdf).”
[^bmw]: BMW Group, “[BMW Group to deploy humanoid robots in production in Germany for the first time](https://www.press.bmwgroup.com/united-kingdom/article/detail/T0455870EN_GB/bmw-group-to-deploy-humanoid-robots-in-production-in-germany-for-the-first-time),” press release.
