# SwarmUI PixAI Tagger v1.0 Extension (GPU)

An advanced image tagging extension for [SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI) powered by the official [PixAI Tagger v1.0](https://huggingface.co/pixai-labs/pixai-tagger-v1.0) model.

Built with direct **GPU acceleration** via the SwarmUI ComfyUI backend and includes a dedicated **Clothing Threshold** controller to independently tune outfit and attire tag sensitivity.

---

## ✨ Features

- **🚀 100% GPU Accelerated**: Executes directly in the ComfyUI PyTorch CUDA runtime (float16) on your graphics card for blazing fast tagging (~50ms) without CPU bottlenecks.
- **👗 Dedicated Clothing Threshold**: Segregates attire, garments, and accessories from general tags, allowing you to set a custom sensitivity specifically for clothing items.
- **🎛️ Per-Category Threshold Sliders**:
  - **General threshold** (default: `0.17`)
  - **Character threshold** (default: `0.27`)
  - **Style threshold** (default: `0.15`)
  - **Copyright / series threshold** (default: `0.24`)
  - **Clothing threshold** (default: `0.17`)
  - **Meta threshold** (default: `0.17`)
  - **Rating threshold** (default: `0.41`)
- **🖱️ Drag & Drop Studio Tool**:
  - Drag and drop any image file directly onto the studio dropzone.
  - Paste images from your clipboard (`Ctrl+V`).
  - View image dimensions and thumbnail preview.
  - Interactive breakdown by category with confidence scores (e.g. `98%`).
  - 1-click copy for individual tags or full prompt output.
  - Direct insertion to prompt box (*Replace*, *Prepend*, or *Append*).
- **👁️ Image Viewer Integration**:
  - `🌸 PixAI Tag` button in the SwarmUI image viewer media button bar.
  - `⚙️ PixAI Studio` button to open the full interactive studio with the viewed image.
- **⚡ Prompt Tag `<pixaitagger>`**:
  - Add `<pixaitagger>` anywhere in your prompt to automatically tag the init image during generation.
  - Optional positional overrides: `<pixaitagger:general_threshold,character_threshold,clothing_threshold>`.
- **⚙️ T2I Parameter Group**:
  - Settings are registered under the **PixAI Tagger** group in SwarmUI's sidebar, fully savable with user presets.
  - Filter tags with wildcard rules (e.g. `tag_to_exclude`, `source:target`, `*hair`, `*dress`).

---

## 📦 Architecture & Requirements

- **Model**: `pixai-labs/pixai-tagger-v1.0` (ViTDet backbone trained on 30,877 tags).
- **Backend**: Runs on ComfyUI's self-start backend using the custom node `PixaiTaggerGenerate`.
- **Dependencies**: `transformers`, `torchvision`, `timm`, `accelerate` (auto-verified and auto-installed if missing).
- **Weights Download**: On first use, Hugging Face automatically downloads the model weights (~1.9 GB) to the local cache.

---

## 🚀 How to Use

### 1. One-Click Tagging from Image Viewer
1. Drag and drop any image into the main SwarmUI image viewer (or select an image from your history).
2. Below the image, click **🌸 PixAI Tag**.
3. The tags are generated on GPU and placed directly into your prompt box.

### 2. Interactive Studio Tool
1. In the viewer, click **⚙️ PixAI Studio** (or open via the tool button).
2. Drag and drop an image or press `Ctrl+V` to paste an image.
3. Fine-tune any category slider (General, Character, Style, Copyright, Clothing, Meta, Rating).
4. Toggle categories on or off using the **Categories in combined tags** pill buttons.
5. Click **🌸 Tag Image on GPU** to see instant categorized tags with confidence badges!

### 3. Generation Prompt Tag
- Insert `<pixaitagger>` in your prompt when generating with an Init Image:
  ```
  masterpiece, best quality, <pixaitagger>
  ```
- Or override thresholds on the fly:
  ```
  <pixaitagger:0.17, 0.27, 0.17>
  ```

---

## 📜 Credits & License

- **PixAI Labs**: Authors of [PixAI Tagger v1.0](https://huggingface.co/pixai-labs/pixai-tagger-v1.0).
- **ComfyUI-MyPixaiTagger**: Inspiration for the ComfyUI node pipeline by [andyleeyuan](https://github.com/andyleeyuan/ComfyUI-MyPixaiTagger).
- **SwarmUI-WD14Tagger**: Inspiration for the extension structure and viewer integration by [Glen Carpenter](https://github.com/GlenCarpenter/SwarmUI-WD14Tagger).
- Released under the **MIT License**.
