# AI 音乐与音效生产候选

> 状态：**探索草案 v0.1**。资料于 2026-09-01 核验，用于首款商业游戏的音频资产 Spike（小规模验证）。它不是法律意见、采购决定或已经稳定的安装指南；服务条款、套餐、模型和授权在实际生成最终资产前必须重新核验并留存快照。

## 1. 当前结论

现阶段不绑定一个“全能网站”，而采用三层候选：

1. **可自动化 / 可本地化主线：Stable Audio 3.0。** 同一模型家族覆盖音乐、音效、Audio-to-Audio、Inpainting 和延长；Small Music / Small SFX / Medium 有开放权重，适合后续建立可复现的批处理。官方称模型使用已许可数据训练、输出归使用者；商业使用仍受 Stability AI Community License 的注册、年收入 100 万美元阈值和其他条款约束。
2. **在线质量基准：ElevenLabs。** Sound Effects 对时长、循环和 API 的游戏适配最直接；Eleven Music v2 有分段编辑、参考音频和 API，适合比较成品质量。音乐条款对 Film、TV 和 `Studio Games` 有套餐限制，后者被定义为商业化且在一个以上平台提供的游戏，因此“未来从 Steam 移植到移动端”可能改变所需授权，不能只看当前 Windows 单平台。
3. **人工后期与游戏验收：REAPER 或 Audacity + Unity。** 生成结果只是原料。最终仍需剪辑、去头尾、修循环、分层、响度与峰值检查、命名、格式导出，并在实际镜头、混音组和目标设备上判断是否抢对白、疲劳或失去反馈辨识度。

第一轮不建议购买多家长期订阅，也不立刻把某个平台写成 Project Skill。先用同一 Brief 做小样对照，确认质量、控制、授权和重复成本，再只保留一条主线与一条备用线。

## 2. 目标生产闭环

```text
玩法事件 / 情绪目标
  → Audio Brief（用途、长度、节奏、材质、视角、禁用项）
  → 同条件生成多份候选
  → 人工筛选与来源检查
  → DAW 剪辑、分层、降噪、循环与响度整理
  → 统一命名、无损母版与游戏压缩版本
  → Unity 导入、AudioMixer / Framework Audio 集成
  → 真实玩法、耳机、扬声器与性能验收
  → 生成资产台账、保留 / 返工 / 替换
```

每个最终资产至少保留：工具和模型版本、生成日期、账号套餐、Prompt / Seed / 参考输入、原始输出、人工修改工程、许可证或条款快照、最终用途和替换历史。授权“可商用”不等于作品必然可获版权保护，也不等于可以把声音作为素材包再次销售、训练模型或登记 Content ID。

## 3. 少而精的平台候选

### 3.1 音效

| 候选 | 最适合 | 优点 | 需要验证的边界 | 当前判断 |
|---|---|---|---|---|
| **Stable Audio 3 Small SFX** | 本地 / API 批量生成 one-shot、环境和机械层 | 开放权重；0.6B 小模型；与音乐模型共用一套技术栈；可做 Audio-to-Audio 与局部编辑 | Windows / GPU 实测速度与显存；Community License 商业注册和收入阈值；输出家族一致性 | **主线 Spike** |
| **ElevenLabs Sound Effects** | 快速得到高质量 one-shot、Foley、氛围和循环 | 可指定 0.1–30 秒；支持无缝循环；非循环音效可导出 48 kHz WAV；有正式 API | 免费层不可商用；Beta 服务不可进入生产；按时长计费；循环 WAV / 批量命名和响度仍需后期 | **在线基准** |
| **Adobe Firefly Generate Sound Effects** | 浏览器中制作单次事件、氛围或按口技节奏对齐画面 | 可用文本和录音控制节奏，也可在短视频 / 音频上叠加；Adobe 将其定位为 commercially safe | 当前不生成音乐或语音；自动化、长循环、变体管理和游戏批量流程较弱 | **人工备选** |
| **Krotos Studio / 专业插件** | 脚步、机械、载具、怪物、冲击等需要持续人工调音的声音族 | 使用授权库和实时/程序化控制；能从同一材料做大量变化；专业声音设计工作流成熟 | 成本较高；Krotos 明确其 AI Ambience Generator 不使用生成式 AI；学习和集成投入要由真实需求证明 | **Later，不是生成主线** |

### 3.2 音乐

| 候选 | 最适合 | 优点 | 需要验证的边界 | 当前判断 |
|---|---|---|---|---|
| **Stable Audio 3 Medium / Small Music** | 可本地化的器乐、短循环、样本、分层素材与迭代编辑 | 开放权重版本；最长可生成数分钟；支持延长、局部重做和已有音频引导；与 SFX 共用工具链 | 本地硬件、长曲结构、主题一致性、可用 Stem 与循环接缝要实测；商业阈值同上 | **主线 Spike** |
| **AIVA Pro** | 需要 MIDI、乐谱式编辑和可继续编曲的器乐 | 250+ 风格；可下载 MIDI 与高质量 WAV；Pro 条款向使用者转让生成曲目的完整版权、期限为永久 | Standard 只允许有限社交平台变现，游戏应按 Pro / 企业条款判断；EULA 禁止机器人操作和未授权私有 API；上传参考会授予平台长期训练许可 | **可编辑音乐优先备选** |
| **Eleven Music v2** | 快速获得接近成品的主题、探索曲和战斗曲基准 | 分段生成与编辑、参考音频、3 秒至 10 分钟、付费计划 API；官方针对游戏媒体 | 自助套餐排除 Film / TV / Studio Games；高保真、PCM、Stem 和多平台权利与套餐绑定；参考素材必须自有权利 | **在线质量基准，授权重点复核** |
| **SOUNDRAW Creator** | 音乐只作为游戏背景、希望快速调长度和结构 | 官方明确 Creator / Artist 计划均可用于游戏背景音乐；商用项目无需逐曲额外付费 | Creator 产物不能当独立音乐销售或登记 Content ID；需确认取消订阅后已下载曲目的持续使用与编辑限制 | **低风险背景音乐备选** |
| **Suno Pro / Premier** | 快速寻找主题、曲风和带人声概念 | 生成速度快；官方允许付费期内生成的歌曲用于电影、电视和电子游戏并商业化 | 免费期作品默认不会因后来订阅而追溯获得商用权；官方也提醒商用授权不保证版权保护；循环、Stem、主题复用和精细交互音乐控制要实测 | **Moodboard / 概念优先，不作为首选最终管线** |

这里的“主线”只表示最值得先测，不表示 Stable Audio 3 已经赢得最终采用。它刚进入 3.0 代，开放权重、本地部署和商业许可很有价值，但可靠性、显存、音质和 Windows 环境成本尚未在本项目证明。

## 4. 后期软件与运行时音频

### DAW / 编辑器

- **REAPER**：当前最值得作为正式 DAW 候选。它适合批量切分、非破坏编辑、循环、区域导出、效果链和脚本化；官方当前个人或年商业收入不超过 20,000 美元可用 60 美元 Discounted License，超出后使用 Commercial License。购买与否等资产 Spike 真正需要多轨和批处理时再决定。
- **Audacity**：免费开源，适合先做波形检查、简单剪辑、淡入淡出和格式转换；如果需要 MIDI、复杂分层、批量区域导出和长期非破坏工程，REAPER 更合适。

### Unity、FMOD 与 Wwise

首个垂直切片优先继续使用 **SSFramework Audio + Unity AudioMixer**，避免为了“专业感”先引入第二套事件、Bank、构建和平台生命周期。只有出现以下真实需求时才评估中间件：

- 同一乐曲需要多个同步 Stem 随玩法连续切换；
- 大量声音变体、参数曲线、空间化和混音需要由非程序人员独立制作；
- Unity 内方案已经造成明显的版本、Profiler、平台或内容生产瓶颈。

当前官方授权可作为未来参考：Wwise Indie 对制作预算不超过 250,000 美元的项目核心许可免费；FMOD 对开发者年收入低于 200,000 美元且制作预算低于 600,000 美元的小型游戏提供 Free Indie License。二者都按项目 / 标题管理，并会引入 SDK、Unity Package、Bank 构建、CI 和平台升级成本，现阶段没有采用收益。

## 5. 第一轮 Audio Spike

使用同一组输入分别测试 Stable Audio 3 与在线基准，预算限制为每个需求保留 3–5 个候选，不追求一次生成最终成品。

### 共用音效包

1. 0.15–0.3 秒、柔和但清晰的 UI 确认音；
2. 0.5–1 秒、有近景材质感的拾取 / 放置音；
3. 8–15 秒、首尾不可察觉的机械或生态运行循环；
4. 15–30 秒、能长期播放而不过度抢占注意力的环境循环；
5. 一组由轻到重、听感属于同一材质族的三档冲击或损坏音。

### 共用音乐包

1. 60–90 秒、可循环的常态探索层；
2. 使用同一动机的 30–60 秒压力层，验证能否自然叠加或切换；
3. 5–10 秒的发现、成功与失败短句；
4. 对同一主题做一次局部修改，验证平台能否保留旋律身份而改变配器或情绪。

### 三款候选的声音关键词

| 游戏 | 音效材料 | 音乐身份 | 应避免 |
|---|---|---|---|
| 《游牧工坊》 | 帆布受风、铜管、继电器、旧电机、远雷、砂砾 | 温暖木质拨弦 + 低速机械脉冲；风暴加入失真打击与低频 | 一味宏大末日、所有设备都用尖锐金属声 |
| 《浮岛复育师》 | 叶片、细流、孢子、昆虫翅、柔风、空腔岩石 | 木管、手碟、轻弦与自然采样；生态恢复后逐层长出声部 | 无变化的“治愈 Lo-fi”、高频鸟叫持续疲劳 |
| 《回声遗迹》 | 石质机关、玻璃晶体、倒放残响、短促时间脉冲 | 不规则钟表节奏 + 稀疏弦乐；回声以同一动机的延迟层出现 | 混响糊成一片、回声效果掩盖战斗反馈 |

### 评分与停止条件

| 维度 | 判断方法 |
|---|---|
| Prompt 命中 | 不看提示词时，听者能否说出材质、距离、动作和情绪 |
| 同族一致 | 轻 / 中 / 重或常态 / 压力版本是否像同一个世界 |
| 可编辑 | 是否有干净前后沿、可用循环、足够动态余量和可分离层 |
| 游戏可读 | 与 VFX、UI、对白和背景音乐同播时是否仍能辨认关键事件 |
| 重复成本 | 第二批是否能沿用 Brief 稳定复现，而非每次重新抽奖 |
| 授权证据 | 能否明确记录生成时套餐、允许的游戏 / 平台、持续使用和禁止项 |

若一个服务连续两批都需要大量修复、无法形成同族声音或授权仍含糊，就停止继续投入，不因为已经付费而迁就它。

## 6. 何时写安装与配置指南

现在只保留调研和实验 Brief。完成第一轮 Spike 后，若某条管线至少两次产出真实进入 Unity 的声音，再补 `docs/ai-game-development-environment.md` 的音频章节，届时才要求用户：

1. 注册被选中的平台和商业套餐；
2. 接受 Hugging Face / Stability 许可证或配置受管 API；
3. 安装已验证版本的 Python / GPU Runtime、REAPER 或 Audacity；
4. 将 API Key 放入个人 Secret / 环境配置，而不是提交仓库；
5. 运行一个可听、可导入、可删除的 smoke，并记录升级与卸载方法。

在流程稳定前创建 `game-audio-generation` Skill 只会固化尚未验证的平台偏好。更合理的触发点是：同一套 Brief、命名、后期、导入和授权检查已经在两个不同声音任务中减少了返工，再把**平台无关的生产闭环**写成 Project Skill；具体网站只作为可替换 Adapter。

## 7. 官方资料

- Stability AI：[Stable Audio 3.0](https://stability.ai/news-updates/meet-stable-audio-3-the-model-family-built-for-artistic-experimentation-with-open-weight-models)、[License](https://stability.ai/license)、[官方 GitHub](https://github.com/Stability-AI/stable-audio-3)
- ElevenLabs：[Sound Effects](https://elevenlabs.io/docs/overview/capabilities/sound-effects)、[Eleven Music](https://elevenlabs.io/docs/eleven-creative/products/music)、[Music Model-Specific Terms](https://elevenlabs.io/eleven-music-model-specific-terms)、[商用内容说明](https://help.elevenlabs.io/hc/en-us/articles/13313564601361-Can-I-publish-the-content-I-generate-on-the-platform)
- Adobe：[Generate Sound Effects](https://helpx.adobe.com/firefly/web/work-with-audio-and-video/work-with-audio/text-to-sound-effects.html)
- AIVA：[产品与套餐](https://www.aiva.ai/)、[EULA](https://www.aiva.ai/legal/1)
- SOUNDRAW：[License](https://soundraw.io/license)、[游戏使用 FAQ](https://docs.channel.io/soundraw-faq/en/articles/Can-I-use-SOUNDRAW-music-in-YouTube-videos-podcasts-ads-or-games-c27c74d3)
- Suno：[付费套餐权利](https://help.suno.com/en/articles/9601665)、[Terms of Service](https://suno.com/terms)
- 后期与中间件：[REAPER License](https://www.reaper.fm/purchase.php)、[Audacity](https://www.audacityteam.org/)、[FMOD Licensing](https://www.fmod.com/licensing)、[Wwise for Games](https://www.audiokinetic.com/pricing/for-games/)
