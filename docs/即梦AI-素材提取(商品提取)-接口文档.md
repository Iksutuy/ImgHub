<span id="4YnpPskB"></span>
# 接口简介

采用智能视觉技术，提取商品主体并智能匹配最佳展示视角，一键生成专业商品展示图。现已支持服装、鞋包、饰品、家具、日用品几大核心品类，助力高效产出高品质商品视觉素材，有效提升视觉吸引力与转化效率。

<span id="fnZcK3m5"></span>
# 接入说明

<span id="NsQVl4r0"></span>
## 限制条件


|名称 |内容 |
|---|---|
|输入图要求 |1. 图片格式：仅支持JPEG、PNG格式，建议使用JPEG格式；<br><br>2. 图片分辨率：最大 4096 \* 4096 |


<span id="Rr0DAuHp"></span>
## 请求说明


|名称 |内容 |
|---|---|
|接口地址 |[https://visual.volcengineapi.com](https://visual.volcengineapi.com/) |
|请求方式 |POST |
|Content\-Type |application/json |


<span id="lKRXiKHX"></span>
## 提交任务

<span id="QyBBoOsr"></span>
### **提交任务请求参数**

<span id="CWiAnggK"></span>
#### **Query参数**

<div data-tips="true" data-tips-type="default">拼接到url后的参数，示例：<a href="https://visual.volcengineapi.com?Action=CVSync2AsyncSubmitTask&Version=2022-08-31">https://visual.volcengineapi.com?Action=CVSync2AsyncSubmitTask&Version=2022\-08\-31</a></div>



|参数 |类型 |**可选/必选** |说明 |
|---|---|---|---|
|Action |string |必选 |接口名，取值：**CVSync2AsyncSubmitTask** |
|Version |string |必选 |版本号，取值：2022\-08\-31 |


<span id="1GrRYD7f"></span>
#### **Header参数**

<div data-tips="true" data-tips-type="warning" data-tips-is-title="true">注意</div>


<div data-tips="true" data-tips-type="warning">本服务固定值：<strong>Region为cn\-north\-1，Service为cv</strong></div>


主要用于鉴权，详见 [公共参数](https://www.volcengine.com/docs/6369/67268) \- 签名参数 \- 在Header中的场景部分

<span id="RPuE6jrg"></span>
#### **Body参数**

<div data-tips="true" data-tips-type="warning" data-tips-is-title="true">注意</div>


<div data-tips="true" data-tips-type="warning">业务请求参数，放到request.body中，MIME\-Type为<strong>application/json</strong></div>



|名称 |类型 |必选 |描述 |
|---|---|---|---|
|req_key |string |必选 |服务标识<br><br>取固定值: **jimeng_i2i_extract_tiled_images** |
|binary_data_base64 |array of string |必选（二选一） |图片文件base64编码，需输入**1**张图片 |
|image_urls |array of string ||图片文件URL，需输入**1**张图片 |
|edit_prompt |string |必选 |编辑指令提示词<br><br>支持以下6种类型（6选1）：<br><br><br>* **提取全身衣服**：提取出图片中的衣服、帽子、鞋子和包，生成一张平铺图，背景为纯白色。<br><br>* **提取鞋子**：提取出图片中的一双鞋子，生成一张正45度图，背景为纯白色。<br><br>* **提取包包**：提取出图片中的完整的包包和包带，正视图，背景为纯白色。<br><br>* **提取沙发**：提取出图片中的完整的沙发，生成一张正视图，背景为纯白色 **。** <br><br>* **提取日用品**：提取出图片中的日用品，生成一张正视图，背景为纯白色。<br><br>* **提取饰品**：提取出图片中的*<ins>饰品</ins>*，生成一张正视图，背景为纯白色。<br><br><br><div data-tips="true" data-tips-type="default" data-tips-is-title="true">说明</div><br><br><br><div data-tips="true" data-tips-type="default"><strong>提取饰品时，饰品可以替换为具体的物品，如耳坠、项链等。</strong>示例：提取出图片中的耳坠，生成一张正视图，背景为纯白色。</div><br> |
|width |int |可选 |生成图片宽<br><br>默认值：2048<br><br>取值范围：[1024, 4096] |
|height |int |可选 |生成图片高<br><br>默认值：2048<br><br>取值范围：[1024, 4096] |
|seed |int |可选 |随机种子，作为确定扩散初始状态的基础，默认\-1（随机）。若随机种子为相同正整数且其他参数均一致，则生成内容极大概率效果一致<br><br>默认值：\-1 |


<span id="wgHtFXjj"></span>
### 提交任务返回参数

<span id="9J5oeUGH"></span>
#### **通用返回参数**

请参考[通用返回字段及错误码](https://www.volcengine.com/docs/6444/69728)

<span id="LLBKpa43"></span>
#### **业务返回参数**

<div data-tips="true" data-tips-type="default" data-tips-is-title="true">重点关注data中以下字段，其他字段为公共返回(可忽略或不做解析)</div>



|字段 |类型 |说明 |
|---|---|---|
|task_id |string |任务ID，用于查询结果 |


<span id="DcCkInuK"></span>
### 提交任务请求&返回完整示例

**请求示例：** 

```JSON
{
    "req_key": "jimeng_i2i_extract_tiled_images",
    // "binary_data_base64": [],
    "image_urls": [
        "https://xxxx",
        // ...
    ],
    "image_edit_prompt": "提取出图片中的衣服、帽子、鞋子和包，生成一张平铺图，背景为纯白色。"
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


<span id="vHlpIqY7"></span>
## 查询任务

<span id="36DgGAwn"></span>
### **查询任务请求参数**

<span id="xMiM8dW0"></span>
#### **Query参数**

<div data-tips="true" data-tips-type="default" data-tips-is-title="true">拼接到url后的参数，示例：<a href="https://visual.volcengineapi.com/">https://visual.volcengineapi.com</a><a href="https://visual.volcengineapi.com?Action=CVGetResult&Version=2022-08-31">?Action=CVSync2AsyncGetResult&Version=2022\-08\-31</a></div>



|参数 |类型 |**可选/必选** |说明 |
|---|---|---|---|
|Action |string |必选 |接口名，固定值：**CVSync2AsyncGetResult** |
|Version |string |必选 |版本号，固定值：**2022\-08\-31** |


<span id="eVQ6Ocv5"></span>
#### **Header参数**

<div data-tips="true" data-tips-type="warning" data-tips-is-title="true">注意</div>


<div data-tips="true" data-tips-type="warning">本服务固定值：<strong>Region为cn\-north\-1，Service为cv</strong></div>


主要用于鉴权，详见 [公共参数](https://www.volcengine.com/docs/6369/67268) \- 签名参数 \- 在Header中的场景部分

<span id="Xtj8YU0u"></span>
#### **Body参数**

<div data-tips="true" data-tips-type="warning" data-tips-is-title="true">注意</div>


<div data-tips="true" data-tips-type="warning">业务请求参数，放到request.body中，MIME\-Type为<strong>application/json</strong></div>



|参数 |类型 |可选/必选 |说明 |
|---|---|---|---|
|req_key |string |必选 |服务标识<br><br>取固定值: **jimeng_i2i_extract_tiled_images** |
|task_id |string |必选 |任务ID，此字段的取值为**提交任务接口**的返回 |
|req_json |JSON string |可选 |json序列化后的字符串<br><br>目前支持水印配置和是否以图片链接形式返回，可在返回结果中添加<br><br>示例："{\"logo_info\":{\"add_logo\":true,\"position\":0,\"language\":0,\"opacity\":1,\"logo_text_content\":\"这里是明水印内容\"},\"return_url\":true}" |


<span id="xVlNT30O"></span>
##### **ReqJson(序列化后的结果再赋值给req_json)** 

配置信息


|**参数** |**类型** |**可选/必选** |**说明** |
|---|---|---|---|
|return_url |bool |可选 |输出是否返回图片链接  **（链接有效期为24小时）**  |
|logo_info |LogoInfo |可选 |水印信息 |
|aigc_meta |AIGCMeta |可选 |隐式标识<br><br>隐式标识验证方式：<br><br><br>1. 查看【png】或【mp4】格式，人工智能生成合成内容表示服务平台（后续预计增加jpg）<br><br>* [https://www.gcmark.com/web/index.html#/mark/check/image](https://www.gcmark.com/web/index.html#/mark/check/image)<br><br>2. 查看【jpg】格式，使用app11 segment查看aigc元数据内容<br><br>* 如 [https://cyber.meme.tips/jpdump/#](https://cyber.meme.tips/jpdump/#) |


<span id="VogaSoUP"></span>
##### **LogoInfo**

水印相关信息


|名称 |类型 |**可选/必选** |描述 |
|---|---|---|---|
|add_logo |bool |可选 |是否添加水印。True为添加，False不添加。默认不添加 |
|position |int |可选 |水印的位置，取值如下：<br><br>0\-右下角<br><br>1\-左下角<br><br>2\-左上角<br><br>3\-右上角<br><br>默认0 |
|language |int |可选 |水印的语言，取值如下：<br><br>0\-中文（AI生成）<br><br>1\-英文（Generated by AI）<br><br>默认0 |
|opacity |float |可选 |水印的不透明度，取值范围0\-1，1表示完全不透明，默认1 |
|logo_text_content |string |可选 |明水印自定义内容 |


<span id="BnDlaxH2"></span>
##### AIGCMeta

隐式标识，依据《人工智能生成合成内容标识办法》&《网络安全技术人工智能生成合成内容标识方法》


|名称 |类型 |**可选/必选** |描述 |
|---|---|---|---|
|content_producer |String |可选 |内容生成服务ID |
|producer_id |String |必选 |内容生成服务商给此图片数据的唯一ID |
|content_propagator |String |可选 |内容传播服务商ID |
|propagate_id |String |可选 |传播服务商给此图片数据的唯一ID |


<span id="978IMqoB"></span>
### 查询任务返回参数

<span id="zCH1F5IN"></span>
#### **通用返回参数**

请参考[通用返回字段及错误码](https://www.volcengine.com/docs/6444/69728)

<span id="kbZNhfzw"></span>
#### **业务返回参数**

<div data-tips="true" data-tips-type="default" data-tips-is-title="true">说明</div>


<div data-tips="true" data-tips-type="default">重点关注data中以下字段，其他字段为公共返回(可忽略或不做解析)</div>



|参数名 |参数说明 |参数示例 |
|---|---|---|
|binary_data_base64 |array of string |返回图片的base64数组。 |
|image_urls |array of string |返回图片的url数组，输出图片格式为jpeg格式（**有效期是24h**） |
|status |string |任务执行状态<br><br><br>* in_queue：任务已提交<br><br>* generating：任务已被消费，处理中<br><br>* done：处理完成，成功或者失败，可根据外层code&message进行判断<br><br>* not_found：任务未找到，可能原因是无此任务或任务已过期(12小时)<br><br>* expired：任务已过期，请尝试重新提交任务请求 |


<span id="Cmpfhal3"></span>
### 查询任务请求&返回完整示例

**请求示例：** 

```JSON
{
    "req_key": "jimeng_i2i_extract_tiled_images",
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
            "https://xxxx"
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


<span id="NEsLCLYD"></span>
## 错误码

<span id="nYfo0kgb"></span>
### **通用错误码**

请参考[通用返回字段及错误码](https://www.volcengine.com/docs/6444/69728)

<span id="alUFueMe"></span>
### **业务错误码**


|HttpCode |错误码 |错误消息 |描述 |是否需要重试 |
|---|---|---|---|---|
|200 |10000 |无 |请求成功 |不需要 |
|400 |50411 |Pre Img Risk Not Pass |输入图片前审核未通过 |不需要 |
|400 |50511 |Post Img Risk Not Pass |输出图片后审核未通过 |可重试 |
|400 |50412 |Text Risk Not Pass |输入文本前审核未通过 |不需要 |
|400 |50512 |Post Text Risk Not Pass |输出文本后审核未通过 |不需要 |
|400 |50413 |Post Text Risk Not Pass |输入文本含敏感词、版权词等审核不通过 |不需要 |
|429 |50429 |Request Has Reached API Limit, Please Try Later |QPS超限 |可重试 |
|429 |50430 |Request Has Reached API Concurrent Limit, Please Try Later |并发超限 |可重试 |
|500 |50500 |Internal Error |内部错误 |不需要 |
|500 |50501 |Internal RPC Error |内部算法错误 |不需要 |


<span id="KOXEO7Ei"></span>
## 接入说明

<span id="bDHTjrni"></span>
### SDK使用说明

请参考[SDK使用说明](https://www.volcengine.com/docs/6444/1340578)

<span id="gRUVA1np"></span>
### HTTP方式接入说明

请参考[HTTP请求示例](https://www.volcengine.com/docs/6444/1390583)

<span id="lZwbcRzq"></span>
### HTTP方式接入说明

请参考[HTTP请求示例](https://www.volcengine.com/docs/6444/1390583)

API_KEY方式接入说明

请参考[API_KEY请求示例](https://docs.volcengine.com/docs/AIGCAIzhongtaigongyongwendang/api-key-usage-guide?lang=zh)



