# Notepad Plus — 铁巢重炮手写板增强

给《Iron Nest: Heavy Turret Simulator》的绘图桌面加两样东西：**更多画笔**和**绘图图层**，
另外顺手加了一条"把线拉长成辅助射线"的工具。

作者：**4Dfish** ｜ 许可：MIT

---

## 功能

### 1. 两支新画笔（蓝、绿）

游戏原本只有红、黄、白三支笔，其中**只有黄笔**既显示方位角和距离、又**不会**把数据写进手写板笔记。
蓝笔和绿笔**与黄笔功能完全一致**：只显示方位角与距离，不写入笔记。

- 多出来的两支笔插在圆规下方，从哪拿出来就放回哪去。
- 画的线、箭头、起点圈、标签全部跟着笔的颜色走，和游戏自己的线用的是同一套材质。
- 红色的"删除提示"不参与改色，免得把游戏的操作提示染掉。

### 2. 四个绘图图层

- `1` `2` `3` `4` 切换图层。**每一笔会归到你落笔时所在的那一层**。
- 切层时只显示当前层，其他层隐藏。上一层画的图不会干扰这一层的测算。
- 四条线各自独立，互不干扰，也不会被别的层的线盖住。

### 3. 叠影模式

- `5` 开关。开启后其他图层不会完全消失，而是以较低透明度保留在画面上，方便对着旧线构图。
- 亮度可在配置里调。

### 4. 画图视角提示条

屏幕正上方居中的一个小条，显示当前图层和叠影开关状态。

**只在对着地图桌按 E 进入画图视角时出现**，其他时候自动隐藏，不挡视线。

### 5. 中键：把线读数记到手写板

**鼠标中键点一条已有的线**，会把这根线的方位角和距离写进手写板笔记。

写入格式直接套用游戏自己的笔记模板（`{angle} / {distance}`），所以**和红笔写出来的完全一样**，
数值也取自画面上显示的那两个标签，与肉眼所见逐字一致。

### 6. L 键：辅助射线

**对着一条已有的线按 `L`**，会从这条线的**出发点**起，沿它自己的方向拉出一条**虚线射线**。

- **到地图页面边缘自动停住**，不会画到桌子外面。
- 外观是克隆这条线自己的线体，所以粗细、发光、配色与游戏一致，只是变成虚线。
- **右键点它即可删除**，和删普通线一样。
- 它算作**当前图层的一员**，切层时会跟着一起隐藏 / 变淡。

---

## 前置要求

- 游戏：**Iron Nest: Heavy Turret Simulator**
- **MelonLoader 0.7.3（IL2CPP 版）** 已正确安装在游戏根目录

本 mod 只针对 IL2CPP 构建，不适用于 Mono 版本。

## 安装

1. 确认游戏根目录已有 `version.dll`、`MelonLoader/` 文件夹（即 MelonLoader 已装好）。
2. 把 `Mods/NotepadPlus.dll` 复制到游戏根目录的 `Mods/` 文件夹里。
3. 启动游戏。

安装只新增文件，不覆盖任何游戏原文件。

## 卸载

删掉 `Mods/NotepadPlus.dll` 即可。游戏本体不受影响。

---

## 按键

| 按键 | 作用 |
|---|---|
| `1` `2` `3` `4` | 切换到第 1~4 个绘图图层 |
| `5` | 开关叠影模式 |
| `L` | 对鼠标指着的线生成辅助射线 |
| **鼠标中键** | 把鼠标指着的线的方位角/距离写进手写板 |
| **鼠标右键** | 删除鼠标指着（附近）的辅助射线 |

按键全部可在配置里改。

## 配置

首次启动后会自动在 `UserData/MelonPreferences.cfg` 里写入一段 `[NotepadPlus]` 配置。

常见可调项（键名即配置文件里的字段名）：

| 键名 | 默认 | 说明 |
|---|---|---|
| `GhostAlpha` | `0.15` | 叠影层的不透明度，0 为全隐、1 为不透明 |
| `PenStartGap` | `2.3` | 第一支新笔与圆规之间的间距（倍数）。**蓝笔和圆规重叠时调大它** |
| `PenGap` | `1.0` | 两支新笔之间的间距（倍数） |
| `HueBlue` / `HueGreen` | `0.60` / `0.33` | 蓝笔、绿笔的色相，取值 0~1 |
| `ShowHud` | `true` | 是否显示画图视角的提示条 |
| `KeyLayer1` ~ `KeyLayer4` | `1`~`4` | 切图层按键 |
| `KeyGhost` | `5` | 叠影开关按键 |
| `VerboseLog` | `false` | 详细日志，排查问题时才需要开 |

改完重启游戏生效。

---

## 已知限制

- **右键可能同时触发游戏的"删除笔迹"**：游戏本身的右键就是删除鼠标指着的线。如果辅助射线正好压在一条线上，右键一下可能两个都删掉。
- **辅助射线只在当前这一场里存在**：切换场景（重开任务）会清空，和游戏自己的线一样。
- **图层划分不会被存档保存**：游戏存档只记录线本身，不记录它属于哪一层。重进任务后所有线会回到第 1 层。
- 提示条的中文使用游戏自带的文字字体渲染；若某个字显示为方框，说明该字体没有收录这个字。

## 从源码编译

```bash
cd src
dotnet build -c Release -p:GameDir="X:\path\to\Iron Nest Heavy Turret Simulator"
```

需要 **.NET SDK 6.0**。`GameDir` 指向游戏根目录，工程会从这里引用 MelonLoader 与游戏的程序集。

产物在 `src/bin/Release/NotepadPlus.dll`。

## 目录结构

```
README.md
LICENSE
.gitignore
src/NotepadPlusMod.cs      源码
src/NotepadPlus.csproj     工程文件
Mods/NotepadPlus.dll       编译好的 mod（玩家直接用这个）
```

---

## English summary

**Notepad Plus** is a MelonLoader mod for *Iron Nest: Heavy Turret Simulator*.

- **Two extra pencils** (blue, green) that work exactly like the game's yellow one — bearing and
  distance readout, no note written.
- **Four plotting layers**, switched with `1`-`4`, with a ghost mode on `5` that keeps the other
  layers faintly visible.
- **A small readout** shown only while the map-table drawing view is open.
- **Middle-click a stroke** to write its bearing and distance onto the notepad, in the game's own
  note format.
- **Press `L` over a stroke** to lay a dashed guide ray from that stroke's starting point along its
  own direction, stopping at the edge of the page. Right-click it to remove it.

Requires MelonLoader 0.7.3 (IL2CPP). Drop `Mods/NotepadPlus.dll` into the game's `Mods` folder.

MIT licensed.
