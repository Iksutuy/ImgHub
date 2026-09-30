<span id="dIiGI4XW"></span>
# 接口简介

即梦4.0是即梦同源的图像生成能力，该能力在统一框架内集成了文生图、图像编辑及多图组合生成的功能：支持单次输入最多 10 张图像及进行复合编辑，并能通过对提示词的深度推理，自动适配最优的图像比例尺寸与生成数量，可一次性输出最多 15 张内容关联的图像。此外，模型显著提升了中文生成的准确率与内容多样性，且支持 4K 超高清输出，为专业图像创作提供了从生成到编辑的一站式解决方案。

<span id="O4rpAIOQ"></span>
# 接入说明

<span id="PsrknMYN"></span>
## 限制条件


|名称 |内容 |
|---|---|
|输入图要求 |1. 图片格式：仅支持JPEG、PNG格式，建议使用JPEG格式；<br><br>2. 图片文件大小：最大 15MB，支持最多输入10张图；<br><br>3. 图片分辨率：最大 4096 \* 4096；<br><br>4. 输入图宽高比：（宽/高）范围：[1/3, 3] |
|输出图说明 |1. 输出图详细宽高规则，参考下方width、height中参数描述；<br><br>2. 输出图会以列表形式返回，最大输出图数量 = 15 \- 输入图数量； |
|其他说明 |1. 输出图片的分辨率越大、输出图片数量越多、输入图片数量越多，延迟增加越明显<br><br>2. 1次调用可能输出多张图片，根据输出图片张数进行计费，默认根据prompt理解意图判断输出图片数量<br><br>3. 若对延迟&价格敏感，建议使用force_single参数，强制只输出1张图片 |


<span id="GqsEOwRc"></span>
## 请求说明


|名称 |内容 |
|---|---|
|接口地址 |[https://visual.volcengineapi.com](https://visual.volcengineapi.com/) |
|请求方式 |POST |
|Content\-Type |application/json |


<span id="0wnQ3Dwc"></span>
## 提交任务

<span id="r4HK4rIK"></span>
### **提交任务请求参数**

<span id="OngKsQoc"></span>
#### **Query参数**

<div data-tips="true" data-tips-type="default">拼接到url后的参数，示例：<a href="https://visual.volcengineapi.com?Action=CVSync2AsyncSubmitTask&Version=2022-08-31">https://visual.volcengineapi.com?Action=CVSync2AsyncSubmitTask&Version=2022\-08\-31</a></div>



|参数 |类型 |**可选/必选** |说明 |
|---|---|---|---|
|Action |string |必选 |接口名，取值：**CVSync2AsyncSubmitTask** |
|Version |string |必选 |版本号，取值：2022\-08\-31 |


<span id="V6PJyija"></span>
#### **Header参数**

<div data-tips="true" data-tips-type="warning" data-tips-is-title="true">注意</div>


<div data-tips="true" data-tips-type="warning">本服务固定值：<strong>Region为cn\-north\-1，Service为cv</strong></div>


主要用于鉴权，详见 [公共参数](https://www.volcengine.com/docs/6369/67268) \- 签名参数 \- 在Header中的场景部分

<span id="UiKDbZzw"></span>
#### **Body参数**

<div data-tips="true" data-tips-type="warning" data-tips-is-title="true">注意</div>


<div data-tips="true" data-tips-type="warning">业务请求参数，放到request.body中，MIME\-Type为<strong>application/json</strong></div>



|名称 |类型 |必选 |描述 |
|---|---|---|---|
|req_key |string |必选 |服务标识<br><br>取固定值: **jimeng_t2i_v40** |
|image_urls |array of string |可选 |图片文件URL，**支持输入0至10张图**<br><br><br>* 图片数量越多&图片尺寸越大，整体服务处理时间越长<br><br>* 输入图片建议控制在6张图片以内，图片过多，参考效果会降低 |
|prompt |string |必选 |用于生成图像的提示词，中英文输入均可 。建议：<br><br><br>* 最长不超过800字符，prompt过长有概率出图异常或不生效<br><br>* 支持 prompt中直接指定生图比例，模型会根据“size”字段智能判断生图宽高比，默认生成2K分辨率、宽高比不超过3的图像；<br><br>* 除“”外不建议输入特殊的符号如$ |
|size |int |可选<br><br>文生图场景<br><br><br>* 面积和宽高需要2选1传入，都不传则默认面积取“2048\*2048”，即生成2K分辨率图像且模型智能判断宽高比；<br><br>* 面积和宽高同时输入时，优先使用宽高；<br><br>* 只传面积，模型会根据用户prompt意图智能判断生图宽高比<br><br><br>图生图场景：<br><br><br>* 结合用户prompt意图、参考图尺寸，由模型智能判断生图宽高比 |生成图片的面积，默认值：4194304 **，** 即2048\*2048，生成2K分辨率图像；<br><br><br>* 取值范围：[1024\*1024, 4096\*4096]，可生成1K到4K分辨率图像；<br><br>* 建议生成2k以上的图片，过小分辨率图片较大概率出现人脸效果不佳、文字异常等效果问题；若2K图片出图效果也不符合预期，可尝试输出更高分辨率图片 |
|width |int ||1、生成图像宽高，默认该字段不传；<br><br>2、宽高乘积在[1024\*1024, 4096\*4096]，且宽高比在[min_ratio, max_ratio]之间<br><br>&nbsp;<br><br>注意：<br><br><br>* 需同时传width和height才会生效<br><br><br>&nbsp;<br><br>推荐可选的宽高：<br><br><br>* 1K<br><br>   * 1024x1024 （1:1）<br><br>* 2K<br><br>   * 2048x2048 （1:1）<br><br>   * 2304x1728（4:3）<br><br>   * 2496x1664 （3:2）<br><br>   * 2560x1440 （16:9）<br><br>   * 3024x1296 （21:9）<br><br>* 4K<br><br>   * 4096x4096 （1:1）<br><br>   * 4694x3520（4:3）<br><br>   * 4992x3328 （3:2）<br><br>   * 5404x3040 （16:9）<br><br>   * 6198x2656 （21:9） |
|height |int |||
|scale |float |可选 |文本描述影响的程度，该值越大代表文本描述影响程度越大，且输入图片影响程度越小（精度：支持小数点后两位）<br><br>默认值：0.5<br><br>取值范围：[0, 1] |
|force_single |bool |可选 |是否强制生成单图<br><br>默认值：false<br><br>&nbsp;<br><br>注意：<br><br><br>* 生成的组图越多，耗时越久，且生成耗时会随图片数量增多而**显著变长**；<br><br>* 如需稳定的组图输出效果，建议prompt控制组图数量在**9张及以内**；<br><br>* 生成组图时，建议prompt明确指明图片分辨率或直接传参具体宽高值，避免模型生成组图分辨率不一致导致接口报错； |
|min_ratio |float |可选 |生图结果的宽/高 ≥ min_ratio，如果智能选比例 < min_ratio，则用min_ratio<br><br>默认值：1/3<br><br>取值范围：[1/16,16) |
|max_ratio |float |可选 |生图结果的宽/高 ≤ max_ratio，如果智能选比例 \> max_ratio，则用max_ratio<br><br>默认值：3<br><br>取值范围：[1/16,16) |
|<div style="text-align: center"><br><strong>异步回调参数说明（下述参数仅对异步回调生效，使用异步轮询调用方式时无需配置）</strong></div><br> ||||
|callback_url |string |可选 |回调接口URL（需公网可访问） |
|return_url |bool |可选 |在使用异步回调时，设置图片资源是否以链接形式返回 （链接有效期为24小时）<br><br>默认值：false |
|logo_info |JSON string |可选 |在使用异步回调时，设置水印信息，详细配置参数请参考下方LogoInfo说明<br><br>传参示例：<br><br>```JSON```<br>```"logo_info": "{\"add_logo\":true,\"position\":0,\"language\":0,\"opacity\":1,\"logo_text_content\":\"这里是明水印内容\"}"```<br> |
|aigc_meta |JSON string |可选 |在使用异步回调时，设置隐式标识，详细配置参数请参考下方AIGCMeta说明<br><br>传参示例：<br><br>```JSON```<br>```"aigc_meta": "{\"content_producer\":\"xxxxxx\", \"producer_id\":\"xxxxxx\", \"content_propagator\":\"xxxxxx\", \"propagate_id\":\"xxxxxx\"}"```<br><br><br>隐式标识验证方式：<br><br><br>1. 查看【png】或【mp4】格式，人工智能生成合成内容表示服务平台（后续预计增加jpg）<br><br>   * [https://www.gcmark.com/web/index.html#/mark/check/image](https://www.gcmark.com/web/index.html#/mark/check/image)<br><br>2. 查看【jpg】格式，使用app11 segment查看aigc元数据内容<br><br>   * 如 [https://cyber.meme.tips/jpdump/#](https://cyber.meme.tips/jpdump/#) |


<span id="wssUZ04s"></span>
##### **LogoInfo (序列化后的结果赋值给**logo_info参数 **)** 

水印配置参数


|名称 |类型 |**可选/必选** |描述 |
|---|---|---|---|
|add_logo |bool |可选 |是否添加水印，true为添加，false不添加<br><br>默认值：false |
|position |int |可选 |水印的位置，取值如下：<br><br>0\-右下角<br><br>1\-左下角<br><br>2\-左上角<br><br>3\-右上角<br><br>默认值：0 |
|language |int |可选 |水印的语言，取值如下：<br><br>0\-中文（AI生成）<br><br>1\-英文（Generated by AI）<br><br>默认值：0 |
|opacity |float |可选 |水印的不透明度，取值范围0\-1，1表示完全不透明，默认1 |
|logo_text_content |string |可选 |明水印自定义内容 |


<span id="HEEP2STe"></span>
##### AIGCMeta  **(序列化后的结果赋值给**aigc_meta参数 **)** 

隐式标识参数，依据《人工智能生成合成内容标识办法》&《网络安全技术人工智能生成合成内容标识方法》


|名称 |类型 |**可选/必选** |描述 |
|---|---|---|---|
|content_producer |String |可选 |内容生成服务ID |
|producer_id |String |必选 |内容生成服务商给此图片数据的唯一ID |
|content_propagator |String |可选 |内容传播服务商ID |
|propagate_id |String |可选 |传播服务商给此图片数据的唯一ID |


<span id="XuqLXili"></span>
### 提交任务返回参数

<span id="1Vcaexl5"></span>
#### **通用返回参数**

请参考[通用返回字段及错误码](https://www.volcengine.com/docs/6444/69728)

<span id="Cn6u5bmF"></span>
#### **业务返回参数**

<div data-tips="true" data-tips-type="default" data-tips-is-title="true">重点关注data中以下字段，其他字段为公共返回(可忽略或不做解析)</div>



|字段 |类型 |说明 |
|---|---|---|
|task_id |string |任务ID，用于查询结果 |


<span id="JsCRrxGU"></span>
### 提交任务请求&返回完整示例

**请求示例：** 

```JSON
{
    "req_key": "jimeng_t2i_v40",
    "image_urls": [
        "https://xxxx",
        // ...
    ],
    "prompt": "背景换成演唱会现场",
    "scale": 0.5
}
```


**返回示例：** 

```JSON
{
    "code": 10000, //状态码，判断状态，code!=10000的情况下，不会返回task_id
    "data": {
        "task_id": "7392616336519610409" //任务ID，查询接口使用
    },
    "message": "Success",
    "request_id": "20240720103939AF0029465CF6A74E51EC", //排查错误的关键信息
    "time_elapsed": "104.852309ms" //链路耗时
}
```


<span id="JHUteyCY"></span>
## 查询任务

<span id="cLIJxaux"></span>
### **查询任务请求参数**

<span id="FVTYxWWm"></span>
#### **Query参数**

<div data-tips="true" data-tips-type="default" data-tips-is-title="true">拼接到url后的参数，示例：<a href="https://visual.volcengineapi.com/">https://visual.volcengineapi.com</a><a href="https://visual.volcengineapi.com?Action=CVGetResult&Version=2022-08-31">?Action=CVSync2AsyncGetResult&Version=2022\-08\-31</a></div>



|参数 |类型 |**可选/必选** |说明 |
|---|---|---|---|
|Action |string |必选 |接口名，固定值：**CVSync2AsyncGetResult** |
|Version |string |必选 |版本号，固定值：**2022\-08\-31** |


<span id="JrwBaH4A"></span>
#### **Header参数**

<div data-tips="true" data-tips-type="warning" data-tips-is-title="true">注意</div>


<div data-tips="true" data-tips-type="warning">本服务固定值：<strong>Region为cn\-north\-1，Service为cv</strong></div>


主要用于鉴权，详见 [公共参数](https://www.volcengine.com/docs/6369/67268) \- 签名参数 \- 在Header中的场景部分

<span id="jXDfMRpe"></span>
#### **Body参数**

<div data-tips="true" data-tips-type="warning" data-tips-is-title="true">注意</div>


<div data-tips="true" data-tips-type="warning">业务请求参数，放到request.body中，MIME\-Type为<strong>application/json</strong></div>



|参数 |类型 |可选/必选 |说明 |
|---|---|---|---|
|req_key |string |必选 |服务标识<br><br>取固定值: **jimeng_t2i_v40** |
|task_id |string |必选 |任务ID，此字段的取值为**提交任务接口**的返回 |
|req_json |JSON string |可选 |json序列化后的字符串<br><br>目前支持水印配置和是否以图片链接形式返回，可在返回结果中添加<br><br>示例："{\"logo_info\":{\"add_logo\":true,\"position\":0,\"language\":0,\"opacity\":1,\"logo_text_content\":\"这里是明水印内容\"},\"return_url\":true}" |


<span id="lMXbhvdz"></span>
##### **ReqJson(序列化后的结果再赋值给req_json)** 

配置信息


|**参数** |**类型** |**可选/必选** |**说明** |
|---|---|---|---|
|return_url |bool |可选 |输出是否返回图片链接  **（链接有效期为24小时）**  |
|logo_info |LogoInfo |可选 |水印信息 |
|aigc_meta |AIGCMeta |可选 |隐式标识<br><br>隐式标识验证方式：<br><br><br>1. 查看【png】或【mp4】格式，人工智能生成合成内容表示服务平台（后续预计增加jpg）<br><br>* [https://www.gcmark.com/web/index.html#/mark/check/image](https://www.gcmark.com/web/index.html#/mark/check/image)<br><br>2. 查看【jpg】格式，使用app11 segment查看aigc元数据内容<br><br>* 如 [https://cyber.meme.tips/jpdump/#](https://cyber.meme.tips/jpdump/#) |


<span id="SKTq6y9R"></span>
##### **LogoInfo**

水印配置参数


|名称 |类型 |**可选/必选** |描述 |
|---|---|---|---|
|add_logo |bool |可选 |是否添加水印，true为添加，false不添加<br><br>默认值：false |
|position |int |可选 |水印的位置，取值如下：<br><br>0\-右下角<br><br>1\-左下角<br><br>2\-左上角<br><br>3\-右上角<br><br>默认值：0 |
|language |int |可选 |水印的语言，取值如下：<br><br>0\-中文（AI生成）<br><br>1\-英文（Generated by AI）<br><br>默认值：0 |
|opacity |float |可选 |水印的不透明度，取值范围0\-1，1表示完全不透明，默认1 |
|logo_text_content |string |可选 |明水印自定义内容 |


<span id="PxqKv1ac"></span>
##### AIGCMeta

隐式标识参数，依据《人工智能生成合成内容标识办法》&《网络安全技术人工智能生成合成内容标识方法》


|名称 |类型 |**可选/必选** |描述 |
|---|---|---|---|
|content_producer |String |可选 |内容生成服务ID |
|producer_id |String |必选 |内容生成服务商给此图片数据的唯一ID |
|content_propagator |String |可选 |内容传播服务商ID |
|propagate_id |String |可选 |传播服务商给此图片数据的唯一ID |


<span id="gBNyimdk"></span>
### 查询任务返回参数

<span id="43JbFxKn"></span>
#### **通用返回参数**

请参考[通用返回字段及错误码](https://www.volcengine.com/docs/6444/69728)

<span id="M2SCSug4"></span>
#### **业务返回参数**

<div data-tips="true" data-tips-type="default" data-tips-is-title="true">说明</div>


<div data-tips="true" data-tips-type="default">重点关注data中以下字段，其他字段为公共返回(可忽略或不做解析)</div>



|参数名 |参数说明 |参数示例 |
|---|---|---|
|binary_data_base64 |array of string |返回图片的base64数组。 |
|image_urls |array of string |返回图片的url数组，输出图片格式为png格式（**有效期是24h**） |
|status |string |任务执行状态<br><br><br>* in_queue：任务已提交<br><br>* generating：任务已被消费，处理中<br><br>* done：处理完成，成功或者失败，可根据外层code&message进行判断<br><br>* not_found：任务未找到，可能原因是无此任务或任务已过期(12小时)<br><br>* expired：任务已过期，请尝试重新提交任务请求 |


<span id="5XZgJBb1"></span>
### 查询任务请求&返回完整示例

**请求示例：** 

```JSON
{
    "req_key": "jimeng_t2i_v40",
    "task_id": "<任务提交接口返回task_id>",
    "req_json":"{\"logo_info\":{\"add_logo\":true,\"position\":0,\"language\":0,\"opacity\":1,\"logo_text_content\":\"这里是明水印内容\"},\"return_url\":true}"
}
```


**返回示例：** 

```JSON
{
    "code": 10000, //状态码，优先判断 code=10000, 然后再判断data.status，否则解析有可能会panic
    "data": {
        "binary_data_base64": null,
        "image_urls": [
            "https://xxxx",
            // ...
        ],
        "status": "done"  //任务状态
    },
    "message": "Success",
    "status": 10000,  //无需关注，请忽略
    "request_id": "2025061718460554C9B78D23B0BAB45B2A",  //排查错误的关键信息
    "time_elapsed": "508.312154ms" //链路耗时
}
```


**返回报错示例：** 

```JSON
{
    "code": 50413, //状态码，优先判断 code=10000, 然后再判断data.status，否则解析有可能会panic
    "data": null, //code!=10000的情况下，该字段返回为null
    "message": "Post Text Risk Not Pass", //错误信息
    "request_id": "202511281418218670D408837A9B0EB58F", //排查错误的关键信息
    "status": 50413, //无需关注，请忽略
    "time_elapsed": "36.799829ms" //链路耗时
}
```


<span id="yyhbEaQm"></span>
## 回调返回说明

<span id="B7WNOBj0"></span>
#### **通用返回参数**

请参考[通用返回字段及错误码](https://www.volcengine.com/docs/6444/69728)

<span id="0WShq6GN"></span>
#### **业务返回参数**

<div data-tips="true" data-tips-type="default" data-tips-is-title="true">说明</div>


<div data-tips="true" data-tips-type="default">重点关注data中以下字段，其他字段为公共返回(可忽略或不做解析)</div>



|参数名 |类型 | |
|---|---|---|
|binary_data_base64 |array of string |返回图片的base64数组 |
|image_urls |array of string |返回图片的url数组，输出图片格式为png格式（**有效期是24h**） |
|status |string |任务执行状态<br><br><br>* in_queue：任务已提交<br><br>* generating：任务已被消费，处理中<br><br>* done：处理完成，成功或者失败，可根据外层code&message进行判断<br><br>* not_found：任务未找到，可能原因是无此任务或任务已过期(12小时)<br><br>* expired：任务已过期，请尝试重新提交任务请求 |


**返回示例：** 

```JSON
{
    "code": 10000, //状态码，优先判断 code=10000, 然后再判断data.status，否则解析有可能会panic
    "task_id": "1016263xxxxx45545367", // 任务ID
    "message": "Success",
    "data": {
        "binary_data_base64": null,
        "image_urls": [
            "https://xxxx",
            // ...
        ],
        "status": "done" //任务状态
    },
    "request_id": "202601221406585EE1349D09B", //排查错误的关键信息
    "time_elapsed": "131.518771ms",
    "status": 10000 //无需关注，请忽略
}
```


**返回报错示例：** 

```JSON
{
        "code": 50215,  //状态码
        "task_id": "1016263xxxxx45545367",  // 任务ID
        "message": "Input invalid for this service.", //错误信息
        "data": {
                "resp_data": ""
        },
        "request_id": "20260122141710E69441A0883C66BBE1",//排查错误的关键信息
        "time_elapsed": "10.974197ms",//链路耗时
        "status": 50215 //无需关注，请忽略
}
```


<span id="zSPGc60E"></span>
## 错误码

<span id="okkRAhsw"></span>
### **通用错误码**

请参考[通用返回字段及错误码](https://www.volcengine.com/docs/6444/69728)

<span id="LiZ6iqrr"></span>
### **业务错误码**


|HttpCode |错误码 |错误消息 |描述 |是否需要重试 |
|---|---|---|---|---|
|200 |10000 |无 |请求成功 |不需要 |
|400 |50411 |Pre Img Risk Not Pass |输入图片前审核未通过 |不需要 |
|400 |50511 |Post Img Risk Not Pass |输出图片后审核未通过 |可重试 |
|400 |50412 |Text Risk Not Pass |输入文本前审核未通过 |不需要 |
|400 |50512 |Post Text Risk Not Pass |输出文本后审核未通过 |不需要 |
|400 |50413 |Post Text Risk Not Pass |输入文本含敏感词、版权词等审核不通过 |不需要 |
|400 |50518 |Pre Img Risk Not Pass: Copyright |输入版权图前审核未通过 |不需要 |
|400 |50519 |Post Img Risk Not Pass: Copyright |输出版权图后审核未通过 |可重试 |
|400 |50520 |Risk Internal Error |审核服务异常 |不需要 |
|400 |50521 |Antidirt Internal Error |版权词服务异常 |不需要 |
|400 |50522 |Image Copyright Internal Error |版权图服务异常 |不需要 |
|429 |50429 |Request Has Reached API Limit, Please Try Later |QPS超限 |可重试 |
|429 |50430 |Request Has Reached API Concurrent Limit, Please Try Later |并发超限 |可重试 |
|500 |50500 |Internal Error |内部错误 |不需要 |
|500 |50501 |Internal RPC Error |内部算法错误 |不需要 |


<span id="BbKrAsaR"></span>
## 接入说明

<span id="SCi3tVo6"></span>
### SDK使用说明

请参考[SDK使用说明](https://www.volcengine.com/docs/6444/1340578)

<span id="BGyXT4Ww"></span>
### HTTP方式接入说明

请参考[HTTP请求示例](https://www.volcengine.com/docs/6444/1390583)

<span id="cP1Phb3u"></span>
### HTTP方式接入说明

请参考[HTTP请求示例](https://www.volcengine.com/docs/6444/1390583)

API_KEY方式接入说明

请参考[API_KEY请求示例](https://docs.volcengine.com/docs/AIGCAIzhongtaigongyongwendang/api-key-usage-guide?lang=zh)

&nbsp;



<span id="dIiGI4XW"></span>
# 接口简介

即梦4.0是即梦同源的图像生成能力，该能力在统一框架内集成了文生图、图像编辑及多图组合生成的功能：支持单次输入最多 10 张图像及进行复合编辑，并能通过对提示词的深度推理，自动适配最优的图像比例尺寸与生成数量，可一次性输出最多 15 张内容关联的图像。此外，模型显著提升了中文生成的准确率与内容多样性，且支持 4K 超高清输出，为专业图像创作提供了从生成到编辑的一站式解决方案。

<span id="O4rpAIOQ"></span>
# 接入说明

<span id="PsrknMYN"></span>
## 限制条件


|名称 |内容 |
|---|---|
|输入图要求 |1. 图片格式：仅支持JPEG、PNG格式，建议使用JPEG格式；<br><br>2. 图片文件大小：最大 15MB，支持最多输入10张图；<br><br>3. 图片分辨率：最大 4096 \* 4096；<br><br>4. 输入图宽高比：（宽/高）范围：[1/3, 3] |
|输出图说明 |1. 输出图详细宽高规则，参考下方width、height中参数描述；<br><br>2. 输出图会以列表形式返回，最大输出图数量 = 15 \- 输入图数量； |
|其他说明 |1. 输出图片的分辨率越大、输出图片数量越多、输入图片数量越多，延迟增加越明显<br><br>2. 1次调用可能输出多张图片，根据输出图片张数进行计费，默认根据prompt理解意图判断输出图片数量<br><br>3. 若对延迟&价格敏感，建议使用force_single参数，强制只输出1张图片 |


<span id="GqsEOwRc"></span>
## 请求说明


|名称 |内容 |
|---|---|
|接口地址 |[https://visual.volcengineapi.com](https://visual.volcengineapi.com/) |
|请求方式 |POST |
|Content\-Type |application/json |


<span id="0wnQ3Dwc"></span>
## 提交任务

<span id="r4HK4rIK"></span>
### **提交任务请求参数**

<span id="OngKsQoc"></span>
#### **Query参数**

<div data-tips="true" data-tips-type="default">拼接到url后的参数，示例：<a href="https://visual.volcengineapi.com?Action=CVSync2AsyncSubmitTask&Version=2022-08-31">https://visual.volcengineapi.com?Action=CVSync2AsyncSubmitTask&Version=2022\-08\-31</a></div>



|参数 |类型 |**可选/必选** |说明 |
|---|---|---|---|
|Action |string |必选 |接口名，取值：**CVSync2AsyncSubmitTask** |
|Version |string |必选 |版本号，取值：2022\-08\-31 |


<span id="V6PJyija"></span>
#### **Header参数**

<div data-tips="true" data-tips-type="warning" data-tips-is-title="true">注意</div>


<div data-tips="true" data-tips-type="warning">本服务固定值：<strong>Region为cn\-north\-1，Service为cv</strong></div>


主要用于鉴权，详见 [公共参数](https://www.volcengine.com/docs/6369/67268) \- 签名参数 \- 在Header中的场景部分

<span id="UiKDbZzw"></span>
#### **Body参数**

<div data-tips="true" data-tips-type="warning" data-tips-is-title="true">注意</div>


<div data-tips="true" data-tips-type="warning">业务请求参数，放到request.body中，MIME\-Type为<strong>application/json</strong></div>



|名称 |类型 |必选 |描述 |
|---|---|---|---|
|req_key |string |必选 |服务标识<br><br>取固定值: **jimeng_t2i_v40** |
|image_urls |array of string |可选 |图片文件URL，**支持输入0至10张图**<br><br><br>* 图片数量越多&图片尺寸越大，整体服务处理时间越长<br><br>* 输入图片建议控制在6张图片以内，图片过多，参考效果会降低 |
|prompt |string |必选 |用于生成图像的提示词，中英文输入均可 。建议：<br><br><br>* 最长不超过800字符，prompt过长有概率出图异常或不生效<br><br>* 支持 prompt中直接指定生图比例，模型会根据“size”字段智能判断生图宽高比，默认生成2K分辨率、宽高比不超过3的图像；<br><br>* 除“”外不建议输入特殊的符号如$ |
|size |int |可选<br><br>文生图场景<br><br><br>* 面积和宽高需要2选1传入，都不传则默认面积取“2048\*2048”，即生成2K分辨率图像且模型智能判断宽高比；<br><br>* 面积和宽高同时输入时，优先使用宽高；<br><br>* 只传面积，模型会根据用户prompt意图智能判断生图宽高比<br><br><br>图生图场景：<br><br><br>* 结合用户prompt意图、参考图尺寸，由模型智能判断生图宽高比 |生成图片的面积，默认值：4194304 **，** 即2048\*2048，生成2K分辨率图像；<br><br><br>* 取值范围：[1024\*1024, 4096\*4096]，可生成1K到4K分辨率图像；<br><br>* 建议生成2k以上的图片，过小分辨率图片较大概率出现人脸效果不佳、文字异常等效果问题；若2K图片出图效果也不符合预期，可尝试输出更高分辨率图片 |
|width |int ||1、生成图像宽高，默认该字段不传；<br><br>2、宽高乘积在[1024\*1024, 4096\*4096]，且宽高比在[min_ratio, max_ratio]之间<br><br>&nbsp;<br><br>注意：<br><br><br>* 需同时传width和height才会生效<br><br><br>&nbsp;<br><br>推荐可选的宽高：<br><br><br>* 1K<br><br>   * 1024x1024 （1:1）<br><br>* 2K<br><br>   * 2048x2048 （1:1）<br><br>   * 2304x1728（4:3）<br><br>   * 2496x1664 （3:2）<br><br>   * 2560x1440 （16:9）<br><br>   * 3024x1296 （21:9）<br><br>* 4K<br><br>   * 4096x4096 （1:1）<br><br>   * 4694x3520（4:3）<br><br>   * 4992x3328 （3:2）<br><br>   * 5404x3040 （16:9）<br><br>   * 6198x2656 （21:9） |
|height |int |||
|scale |float |可选 |文本描述影响的程度，该值越大代表文本描述影响程度越大，且输入图片影响程度越小（精度：支持小数点后两位）<br><br>默认值：0.5<br><br>取值范围：[0, 1] |
|force_single |bool |可选 |是否强制生成单图<br><br>默认值：false<br><br>&nbsp;<br><br>注意：<br><br><br>* 生成的组图越多，耗时越久，且生成耗时会随图片数量增多而**显著变长**；<br><br>* 如需稳定的组图输出效果，建议prompt控制组图数量在**9张及以内**；<br><br>* 生成组图时，建议prompt明确指明图片分辨率或直接传参具体宽高值，避免模型生成组图分辨率不一致导致接口报错； |
|min_ratio |float |可选 |生图结果的宽/高 ≥ min_ratio，如果智能选比例 < min_ratio，则用min_ratio<br><br>默认值：1/3<br><br>取值范围：[1/16,16) |
|max_ratio |float |可选 |生图结果的宽/高 ≤ max_ratio，如果智能选比例 \> max_ratio，则用max_ratio<br><br>默认值：3<br><br>取值范围：[1/16,16) |
|<div style="text-align: center"><br><strong>异步回调参数说明（下述参数仅对异步回调生效，使用异步轮询调用方式时无需配置）</strong></div><br> ||||
|callback_url |string |可选 |回调接口URL（需公网可访问） |
|return_url |bool |可选 |在使用异步回调时，设置图片资源是否以链接形式返回 （链接有效期为24小时）<br><br>默认值：false |
|logo_info |JSON string |可选 |在使用异步回调时，设置水印信息，详细配置参数请参考下方LogoInfo说明<br><br>传参示例：<br><br>```JSON```<br>```"logo_info": "{\"add_logo\":true,\"position\":0,\"language\":0,\"opacity\":1,\"logo_text_content\":\"这里是明水印内容\"}"```<br> |
|aigc_meta |JSON string |可选 |在使用异步回调时，设置隐式标识，详细配置参数请参考下方AIGCMeta说明<br><br>传参示例：<br><br>```JSON```<br>```"aigc_meta": "{\"content_producer\":\"xxxxxx\", \"producer_id\":\"xxxxxx\", \"content_propagator\":\"xxxxxx\", \"propagate_id\":\"xxxxxx\"}"```<br><br><br>隐式标识验证方式：<br><br><br>1. 查看【png】或【mp4】格式，人工智能生成合成内容表示服务平台（后续预计增加jpg）<br><br>   * [https://www.gcmark.com/web/index.html#/mark/check/image](https://www.gcmark.com/web/index.html#/mark/check/image)<br><br>2. 查看【jpg】格式，使用app11 segment查看aigc元数据内容<br><br>   * 如 [https://cyber.meme.tips/jpdump/#](https://cyber.meme.tips/jpdump/#) |


<span id="wssUZ04s"></span>
##### **LogoInfo (序列化后的结果赋值给**logo_info参数 **)** 

水印配置参数


|名称 |类型 |**可选/必选** |描述 |
|---|---|---|---|
|add_logo |bool |可选 |是否添加水印，true为添加，false不添加<br><br>默认值：false |
|position |int |可选 |水印的位置，取值如下：<br><br>0\-右下角<br><br>1\-左下角<br><br>2\-左上角<br><br>3\-右上角<br><br>默认值：0 |
|language |int |可选 |水印的语言，取值如下：<br><br>0\-中文（AI生成）<br><br>1\-英文（Generated by AI）<br><br>默认值：0 |
|opacity |float |可选 |水印的不透明度，取值范围0\-1，1表示完全不透明，默认1 |
|logo_text_content |string |可选 |明水印自定义内容 |


<span id="HEEP2STe"></span>
##### AIGCMeta  **(序列化后的结果赋值给**aigc_meta参数 **)** 

隐式标识参数，依据《人工智能生成合成内容标识办法》&《网络安全技术人工智能生成合成内容标识方法》


|名称 |类型 |**可选/必选** |描述 |
|---|---|---|---|
|content_producer |String |可选 |内容生成服务ID |
|producer_id |String |必选 |内容生成服务商给此图片数据的唯一ID |
|content_propagator |String |可选 |内容传播服务商ID |
|propagate_id |String |可选 |传播服务商给此图片数据的唯一ID |


<span id="XuqLXili"></span>
### 提交任务返回参数

<span id="1Vcaexl5"></span>
#### **通用返回参数**

请参考[通用返回字段及错误码](https://www.volcengine.com/docs/6444/69728)

<span id="Cn6u5bmF"></span>
#### **业务返回参数**

<div data-tips="true" data-tips-type="default" data-tips-is-title="true">重点关注data中以下字段，其他字段为公共返回(可忽略或不做解析)</div>



|字段 |类型 |说明 |
|---|---|---|
|task_id |string |任务ID，用于查询结果 |


<span id="JsCRrxGU"></span>
### 提交任务请求&返回完整示例

**请求示例：** 

```JSON
{
    "req_key": "jimeng_t2i_v40",
    "image_urls": [
        "https://xxxx",
        // ...
    ],
    "prompt": "背景换成演唱会现场",
    "scale": 0.5
}
```


**返回示例：** 

```JSON
{
    "code": 10000, //状态码，判断状态，code!=10000的情况下，不会返回task_id
    "data": {
        "task_id": "7392616336519610409" //任务ID，查询接口使用
    },
    "message": "Success",
    "request_id": "20240720103939AF0029465CF6A74E51EC", //排查错误的关键信息
    "time_elapsed": "104.852309ms" //链路耗时
}
```


<span id="JHUteyCY"></span>
## 查询任务

<span id="cLIJxaux"></span>
### **查询任务请求参数**

<span id="FVTYxWWm"></span>
#### **Query参数**

<div data-tips="true" data-tips-type="default" data-tips-is-title="true">拼接到url后的参数，示例：<a href="https://visual.volcengineapi.com/">https://visual.volcengineapi.com</a><a href="https://visual.volcengineapi.com?Action=CVGetResult&Version=2022-08-31">?Action=CVSync2AsyncGetResult&Version=2022\-08\-31</a></div>



|参数 |类型 |**可选/必选** |说明 |
|---|---|---|---|
|Action |string |必选 |接口名，固定值：**CVSync2AsyncGetResult** |
|Version |string |必选 |版本号，固定值：**2022\-08\-31** |


<span id="JrwBaH4A"></span>
#### **Header参数**

<div data-tips="true" data-tips-type="warning" data-tips-is-title="true">注意</div>


<div data-tips="true" data-tips-type="warning">本服务固定值：<strong>Region为cn\-north\-1，Service为cv</strong></div>


主要用于鉴权，详见 [公共参数](https://www.volcengine.com/docs/6369/67268) \- 签名参数 \- 在Header中的场景部分

<span id="jXDfMRpe"></span>
#### **Body参数**

<div data-tips="true" data-tips-type="warning" data-tips-is-title="true">注意</div>


<div data-tips="true" data-tips-type="warning">业务请求参数，放到request.body中，MIME\-Type为<strong>application/json</strong></div>



|参数 |类型 |可选/必选 |说明 |
|---|---|---|---|
|req_key |string |必选 |服务标识<br><br>取固定值: **jimeng_t2i_v40** |
|task_id |string |必选 |任务ID，此字段的取值为**提交任务接口**的返回 |
|req_json |JSON string |可选 |json序列化后的字符串<br><br>目前支持水印配置和是否以图片链接形式返回，可在返回结果中添加<br><br>示例："{\"logo_info\":{\"add_logo\":true,\"position\":0,\"language\":0,\"opacity\":1,\"logo_text_content\":\"这里是明水印内容\"},\"return_url\":true}" |


<span id="lMXbhvdz"></span>
##### **ReqJson(序列化后的结果再赋值给req_json)** 

配置信息


|**参数** |**类型** |**可选/必选** |**说明** |
|---|---|---|---|
|return_url |bool |可选 |输出是否返回图片链接  **（链接有效期为24小时）**  |
|logo_info |LogoInfo |可选 |水印信息 |
|aigc_meta |AIGCMeta |可选 |隐式标识<br><br>隐式标识验证方式：<br><br><br>1. 查看【png】或【mp4】格式，人工智能生成合成内容表示服务平台（后续预计增加jpg）<br><br>* [https://www.gcmark.com/web/index.html#/mark/check/image](https://www.gcmark.com/web/index.html#/mark/check/image)<br><br>2. 查看【jpg】格式，使用app11 segment查看aigc元数据内容<br><br>* 如 [https://cyber.meme.tips/jpdump/#](https://cyber.meme.tips/jpdump/#) |


<span id="SKTq6y9R"></span>
##### **LogoInfo**

水印配置参数


|名称 |类型 |**可选/必选** |描述 |
|---|---|---|---|
|add_logo |bool |可选 |是否添加水印，true为添加，false不添加<br><br>默认值：false |
|position |int |可选 |水印的位置，取值如下：<br><br>0\-右下角<br><br>1\-左下角<br><br>2\-左上角<br><br>3\-右上角<br><br>默认值：0 |
|language |int |可选 |水印的语言，取值如下：<br><br>0\-中文（AI生成）<br><br>1\-英文（Generated by AI）<br><br>默认值：0 |
|opacity |float |可选 |水印的不透明度，取值范围0\-1，1表示完全不透明，默认1 |
|logo_text_content |string |可选 |明水印自定义内容 |


<span id="PxqKv1ac"></span>
##### AIGCMeta

隐式标识参数，依据《人工智能生成合成内容标识办法》&《网络安全技术人工智能生成合成内容标识方法》


|名称 |类型 |**可选/必选** |描述 |
|---|---|---|---|
|content_producer |String |可选 |内容生成服务ID |
|producer_id |String |必选 |内容生成服务商给此图片数据的唯一ID |
|content_propagator |String |可选 |内容传播服务商ID |
|propagate_id |String |可选 |传播服务商给此图片数据的唯一ID |


<span id="gBNyimdk"></span>
### 查询任务返回参数

<span id="43JbFxKn"></span>
#### **通用返回参数**

请参考[通用返回字段及错误码](https://www.volcengine.com/docs/6444/69728)

<span id="M2SCSug4"></span>
#### **业务返回参数**

<div data-tips="true" data-tips-type="default" data-tips-is-title="true">说明</div>


<div data-tips="true" data-tips-type="default">重点关注data中以下字段，其他字段为公共返回(可忽略或不做解析)</div>



|参数名 |参数说明 |参数示例 |
|---|---|---|
|binary_data_base64 |array of string |返回图片的base64数组。 |
|image_urls |array of string |返回图片的url数组，输出图片格式为png格式（**有效期是24h**） |
|status |string |任务执行状态<br><br><br>* in_queue：任务已提交<br><br>* generating：任务已被消费，处理中<br><br>* done：处理完成，成功或者失败，可根据外层code&message进行判断<br><br>* not_found：任务未找到，可能原因是无此任务或任务已过期(12小时)<br><br>* expired：任务已过期，请尝试重新提交任务请求 |


<span id="5XZgJBb1"></span>
### 查询任务请求&返回完整示例

**请求示例：** 

```JSON
{
    "req_key": "jimeng_t2i_v40",
    "task_id": "<任务提交接口返回task_id>",
    "req_json":"{\"logo_info\":{\"add_logo\":true,\"position\":0,\"language\":0,\"opacity\":1,\"logo_text_content\":\"这里是明水印内容\"},\"return_url\":true}"
}
```


**返回示例：** 

```JSON
{
    "code": 10000, //状态码，优先判断 code=10000, 然后再判断data.status，否则解析有可能会panic
    "data": {
        "binary_data_base64": null,
        "image_urls": [
            "https://xxxx",
            // ...
        ],
        "status": "done"  //任务状态
    },
    "message": "Success",
    "status": 10000,  //无需关注，请忽略
    "request_id": "2025061718460554C9B78D23B0BAB45B2A",  //排查错误的关键信息
    "time_elapsed": "508.312154ms" //链路耗时
}
```


**返回报错示例：** 

```JSON
{
    "code": 50413, //状态码，优先判断 code=10000, 然后再判断data.status，否则解析有可能会panic
    "data": null, //code!=10000的情况下，该字段返回为null
    "message": "Post Text Risk Not Pass", //错误信息
    "request_id": "202511281418218670D408837A9B0EB58F", //排查错误的关键信息
    "status": 50413, //无需关注，请忽略
    "time_elapsed": "36.799829ms" //链路耗时
}
```


<span id="yyhbEaQm"></span>
## 回调返回说明

<span id="B7WNOBj0"></span>
#### **通用返回参数**

请参考[通用返回字段及错误码](https://www.volcengine.com/docs/6444/69728)

<span id="0WShq6GN"></span>
#### **业务返回参数**

<div data-tips="true" data-tips-type="default" data-tips-is-title="true">说明</div>


<div data-tips="true" data-tips-type="default">重点关注data中以下字段，其他字段为公共返回(可忽略或不做解析)</div>



|参数名 |类型 | |
|---|---|---|
|binary_data_base64 |array of string |返回图片的base64数组 |
|image_urls |array of string |返回图片的url数组，输出图片格式为png格式（**有效期是24h**） |
|status |string |任务执行状态<br><br><br>* in_queue：任务已提交<br><br>* generating：任务已被消费，处理中<br><br>* done：处理完成，成功或者失败，可根据外层code&message进行判断<br><br>* not_found：任务未找到，可能原因是无此任务或任务已过期(12小时)<br><br>* expired：任务已过期，请尝试重新提交任务请求 |


**返回示例：** 

```JSON
{
    "code": 10000, //状态码，优先判断 code=10000, 然后再判断data.status，否则解析有可能会panic
    "task_id": "1016263xxxxx45545367", // 任务ID
    "message": "Success",
    "data": {
        "binary_data_base64": null,
        "image_urls": [
            "https://xxxx",
            // ...
        ],
        "status": "done" //任务状态
    },
    "request_id": "202601221406585EE1349D09B", //排查错误的关键信息
    "time_elapsed": "131.518771ms",
    "status": 10000 //无需关注，请忽略
}
```


**返回报错示例：** 

```JSON
{
        "code": 50215,  //状态码
        "task_id": "1016263xxxxx45545367",  // 任务ID
        "message": "Input invalid for this service.", //错误信息
        "data": {
                "resp_data": ""
        },
        "request_id": "20260122141710E69441A0883C66BBE1",//排查错误的关键信息
        "time_elapsed": "10.974197ms",//链路耗时
        "status": 50215 //无需关注，请忽略
}
```


<span id="zSPGc60E"></span>
## 错误码

<span id="okkRAhsw"></span>
### **通用错误码**

请参考[通用返回字段及错误码](https://www.volcengine.com/docs/6444/69728)

<span id="LiZ6iqrr"></span>
### **业务错误码**


|HttpCode |错误码 |错误消息 |描述 |是否需要重试 |
|---|---|---|---|---|
|200 |10000 |无 |请求成功 |不需要 |
|400 |50411 |Pre Img Risk Not Pass |输入图片前审核未通过 |不需要 |
|400 |50511 |Post Img Risk Not Pass |输出图片后审核未通过 |可重试 |
|400 |50412 |Text Risk Not Pass |输入文本前审核未通过 |不需要 |
|400 |50512 |Post Text Risk Not Pass |输出文本后审核未通过 |不需要 |
|400 |50413 |Post Text Risk Not Pass |输入文本含敏感词、版权词等审核不通过 |不需要 |
|400 |50518 |Pre Img Risk Not Pass: Copyright |输入版权图前审核未通过 |不需要 |
|400 |50519 |Post Img Risk Not Pass: Copyright |输出版权图后审核未通过 |可重试 |
|400 |50520 |Risk Internal Error |审核服务异常 |不需要 |
|400 |50521 |Antidirt Internal Error |版权词服务异常 |不需要 |
|400 |50522 |Image Copyright Internal Error |版权图服务异常 |不需要 |
|429 |50429 |Request Has Reached API Limit, Please Try Later |QPS超限 |可重试 |
|429 |50430 |Request Has Reached API Concurrent Limit, Please Try Later |并发超限 |可重试 |
|500 |50500 |Internal Error |内部错误 |不需要 |
|500 |50501 |Internal RPC Error |内部算法错误 |不需要 |


<span id="BbKrAsaR"></span>
## 接入说明

<span id="SCi3tVo6"></span>
### SDK使用说明

请参考[SDK使用说明](https://www.volcengine.com/docs/6444/1340578)

<span id="BGyXT4Ww"></span>
### HTTP方式接入说明

请参考[HTTP请求示例](https://www.volcengine.com/docs/6444/1390583)

<span id="cP1Phb3u"></span>
### HTTP方式接入说明

请参考[HTTP请求示例](https://www.volcengine.com/docs/6444/1390583)

API_KEY方式接入说明

请参考[API_KEY请求示例](https://docs.volcengine.com/docs/AIGCAIzhongtaigongyongwendang/api-key-usage-guide?lang=zh)

&nbsp;



