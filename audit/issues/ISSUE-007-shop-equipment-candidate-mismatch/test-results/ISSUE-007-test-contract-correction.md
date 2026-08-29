# 测试契约修正记录

首次运行 `CompactRegionsStayEmptyOnOtherNativeShopCaptures` 时，测试错误地
遍历了四个参考卡位，包括画面中没有角色的空卡占位框。

- 运行结果：6 项中 5 通过、1 失败；
- 失败素材：`reward_shop_after_two_purchases.jpg`；
- 失败位置：未占用的 Front 参考卡位 1 的三个子区域均被装饰线条判为有前景；
- 失败断言：`Expected False / Actual True`。

源码核对 `Phase2OperationalScreenshotAnalyzer.cs` 后确认，生产代码只会对
已经识别出的 `CharacterCardSlotRecognition` 调用 `RecognizeEquipmentSlots`；
未占用的卡位不会进入装备识别。原测试把生产调用域之外的占位框当成装备空槽，
属于测试契约过宽，不是产品回归。

修正只给四张截图补上目视可见的已占用 Front 卡数（3、3、1、1），仍对每个
实际角色的三个装备槽执行 `HasCenteredEquipmentForeground` 断言。没有修改
生产代码、前景算法或期望的装备状态。修正后同一测试文件 6/6 通过，正式原始
结果见 `ISSUE-007-after-native-shop-regression.trx`。

首次失败 TRX 曾使用同一输出文件名，随后被 VSTest 明确提示覆盖；因此这里保存
完整失败原因和契约修正，而不把被覆盖文件宣称为仍存在的原始证据。
