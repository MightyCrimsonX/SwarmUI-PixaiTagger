"""ComfyUI custom node for PixAI Tagger v1.0 with GPU acceleration and clothing thresholding."""

import json
import os
import re
import sys
import subprocess
import time
import numpy as np
from PIL import Image
import torch

MODEL_ID = "pixai-labs/pixai-tagger-v1.0"
_tagger = None
_clothing_set = None

CLOTHING_SUFFIXES = (
    "_dress", "_skirt", "_shirt", "_pants", "_shorts", "_jacket", "_coat",
    "_hoodie", "_sweater", "_cardigan", "_vest", "_suit", "_uniform",
    "_swimsuit", "_bikini", "_underwear", "_panties", "_bra", "_socks",
    "_stockings", "_tights", "_thighhighs", "_kneehighs", "_boots", "_shoes",
    "_sneakers", "_sandals", "_slippers", "_gloves", "_mittens", "_hat",
    "_cap", "_beret", "_hood", "_ribbon", "_bow", "_tie", "_necktie",
    "_bowtie", "_scarf", "_belt", "_apron", "_cape", "_cloak", "_kimono",
    "_yukata", "_hakama", "_haori", "_costume", "_outfit", "_sleeves",
    "_collar", "_headband", "_mask", "_leotard", "_bodysuit", "_corset",
    "_choker", "_necklace", "_earrings", "_bracelet", "_wristband", "_anklet",
    "_glasses", "_sunglasses", "_veil", "_crown", "_tiara", "_bandeau",
    "_bloomers", "_tutu", "_tunic", "_shawl", "_legwear", "_footwear",
    "_headwear", "_garter", "_heels", "_pumps", "_loafers", "_headdress"
)


def ensure_dependencies():
    """Ensure required packages are available in the current Python environment."""
    required = ["transformers", "timm", "torchvision", "accelerate"]
    missing = []
    for pkg in required:
        try:
            __import__(pkg)
        except ImportError:
            missing.append(pkg)
    if missing:
        print(f"[PixAITagger] Installing missing dependencies in {sys.executable}: {', '.join(missing)}")
        try:
            subprocess.run([sys.executable, "-m", "pip", "install", *missing], check=True)
            print("[PixAITagger] Dependencies installed successfully.")
        except Exception as exc:
            print(f"[PixAITagger] Warning: Failed to auto-install dependencies: {exc}")


def load_clothing_tags():
    """Load Danbooru clothing & attire tags from local json file."""
    global _clothing_set
    if _clothing_set is not None:
        return _clothing_set

    _clothing_set = set()
    json_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "clothing_tags.json")
    if os.path.exists(json_path):
        try:
            with open(json_path, "r", encoding="utf-8") as f:
                tags = json.load(f)
                _clothing_set = {t.strip().lower().replace(" ", "_") for t in tags if t.strip()}
        except Exception as e:
            print(f"[PixAITagger] Warning: Failed to load clothing_tags.json: {e}")

    return _clothing_set


def is_clothing_tag(tag):
    """Determine whether a general booru tag belongs to the clothing / attire category."""
    norm_tag = tag.strip().lower().replace(" ", "_")
    clothing_set = load_clothing_tags()
    if norm_tag in clothing_set:
        return True
    for suffix in CLOTHING_SUFFIXES:
        if norm_tag.endswith(suffix):
            return True
    return False


def get_tagger():
    """Load the official PixAI Tagger v1.0 pipeline on GPU."""
    global _tagger
    if _tagger is not None:
        return _tagger

    ensure_dependencies()
    from transformers import pipeline

    device = -1
    torch_dtype = torch.float32

    # Check for ComfyUI model management device or PyTorch CUDA
    try:
        import comfy.model_management
        dev = comfy.model_management.get_torch_device()
        if "cuda" in str(dev).lower():
            device = 0
            torch_dtype = torch.float16
            print(f"[PixAITagger] Using GPU acceleration via ComfyUI device: {dev}")
        elif "mps" in str(dev).lower():
            device = "mps"
            print(f"[PixAITagger] Using Apple Silicon MPS device: {dev}")
    except Exception:
        pass

    if device == -1 and torch.cuda.is_available():
        device = 0
        torch_dtype = torch.float16
        print("[PixAITagger] Using GPU acceleration (CUDA:0) with float16")

    _tagger = pipeline(
        model=MODEL_ID,
        image_processor=MODEL_ID,
        trust_remote_code=True,
        device=device,
        torch_dtype=torch_dtype,
    )
    print(f"[PixAITagger] Pipeline loaded successfully on device {device}.")
    return _tagger


def to_pil_image(image_tensor):
    """Convert a ComfyUI image tensor [H, W, C] to a PIL RGB Image."""
    image_array = image_tensor.detach().cpu().numpy()
    image_array = np.clip(image_array * 255.0, 0, 255).astype(np.uint8)
    img = Image.fromarray(image_array)
    if img.mode != "RGB":
        img = img.convert("RGB")
    return img


def parse_exclusions(exclude_text):
    """Parse comma/newline separated tag exclusion rules."""
    if not exclude_text:
        return set()
    return {
        tag.strip().lower().replace(" ", "_")
        for tag in re.split(r"[,\n]", exclude_text)
        if tag.strip()
    }


def format_tag_items(tag_items, excluded, replace_underscore, include_confidence):
    """Filter, sort by confidence score descending, and format tag list."""
    filtered = []
    for tag, score in tag_items:
        norm = tag.strip().lower().replace(" ", "_")
        if norm in excluded:
            continue
        display_name = tag.replace("_", " ") if replace_underscore else tag
        if include_confidence:
            filtered.append((f"{display_name}:{score:.2f}", score))
        else:
            filtered.append((display_name, score))

    filtered.sort(key=lambda item: item[1], reverse=True)
    return [name for name, _ in filtered]


class PixaiTaggerGenerate:
    """ComfyUI custom node for PixAI Tagger v1.0 with GPU acceleration and clothing thresholding."""

    OUTPUT_NODE = True

    @classmethod
    def INPUT_TYPES(cls):
        return {
            "required": {
                "images": ("IMAGE",),
                "general_threshold": ("FLOAT", {"default": 0.17, "min": 0.0, "max": 1.0, "step": 0.01}),
                "character_threshold": ("FLOAT", {"default": 0.27, "min": 0.0, "max": 1.0, "step": 0.01}),
                "style_threshold": ("FLOAT", {"default": 0.15, "min": 0.0, "max": 1.0, "step": 0.01}),
                "copyright_threshold": ("FLOAT", {"default": 0.24, "min": 0.0, "max": 1.0, "step": 0.01}),
                "clothing_threshold": ("FLOAT", {"default": 0.17, "min": 0.0, "max": 1.0, "step": 0.01}),
                "meta_threshold": ("FLOAT", {"default": 0.17, "min": 0.0, "max": 1.0, "step": 0.01}),
                "rating_threshold": ("FLOAT", {"default": 0.41, "min": 0.0, "max": 1.0, "step": 0.01}),
                "enable_general": ("BOOLEAN", {"default": True}),
                "enable_character": ("BOOLEAN", {"default": True}),
                "enable_style": ("BOOLEAN", {"default": True}),
                "enable_copyright": ("BOOLEAN", {"default": True}),
                "enable_clothing": ("BOOLEAN", {"default": True}),
                "enable_meta": ("BOOLEAN", {"default": False}),
                "enable_rating": ("BOOLEAN", {"default": False}),
                "include_confidence": ("BOOLEAN", {"default": False}),
                "keep_underscores": ("BOOLEAN", {"default": False}),
                "exclude_tags": ("STRING", {"multiline": True, "default": ""}),
            },
            "optional": {
                "output_path": ("STRING", {"default": ""}),
            }
        }

    RETURN_TYPES = ("STRING", "STRING", "STRING", "STRING", "STRING", "STRING", "STRING", "STRING")
    RETURN_NAMES = ("combined_tags", "character", "copyright", "style", "clothing", "general", "meta", "rating")
    FUNCTION = "generate_tags"
    CATEGORY = "ImageTagging/PixAI"

    def generate_tags(
        self,
        images,
        general_threshold,
        character_threshold,
        style_threshold,
        copyright_threshold,
        clothing_threshold,
        meta_threshold,
        rating_threshold,
        enable_general,
        enable_character,
        enable_style,
        enable_copyright,
        enable_clothing,
        enable_meta,
        enable_rating,
        include_confidence,
        keep_underscores,
        exclude_tags="",
        output_path="",
    ):
        t0 = time.time()
        tagger = get_tagger()
        excluded = parse_exclusions(exclude_tags)
        replace_underscore = not keep_underscores

        # General category in model contains clothing. Use minimum of both thresholds to capture all candidates.
        min_gen_thresh = min(general_threshold, clothing_threshold)
        pipeline_thresholds = {
            "general": min_gen_thresh,
            "character": character_threshold,
            "style": style_threshold,
            "copyright": copyright_threshold,
            "meta": meta_threshold,
            "rating": rating_threshold,
        }

        # Convert image batch to PIL
        pil_images = [to_pil_image(img) for img in images]
        raw_results = tagger(pil_images, threshold=pipeline_thresholds, batch_size=len(pil_images))
        inference_time = time.time() - t0
        if isinstance(raw_results, dict):
            raw_results = [raw_results]

        all_combined = []
        all_character = []
        all_copyright = []
        all_style = []
        all_clothing = []
        all_general = []
        all_meta = []
        all_rating = []
        details_summary = None

        for result in raw_results:
            cats = result.get("results", {})

            # Segregate clothing tags from general tags
            raw_general_items = list(cats.get("general", {}).items())
            clothing_items = []
            general_items = []

            for tag, score in raw_general_items:
                if is_clothing_tag(tag):
                    if score >= clothing_threshold and enable_clothing:
                        clothing_items.append((tag, score))
                else:
                    if score >= general_threshold and enable_general:
                        general_items.append((tag, score))

            character_items = [
                (tag, score) for tag, score in cats.get("character", {}).items()
                if score >= character_threshold and enable_character
            ]
            style_items = [
                (tag, score) for tag, score in cats.get("style", {}).items()
                if score >= style_threshold and enable_style
            ]
            copyright_items = [
                (tag, score) for tag, score in cats.get("copyright", {}).items()
                if score >= copyright_threshold and enable_copyright
            ]
            meta_items = [
                (tag, score) for tag, score in cats.get("meta", {}).items()
                if score >= meta_threshold and enable_meta
            ]
            rating_items = [
                (tag, score) for tag, score in cats.get("rating", {}).items()
                if score >= rating_threshold and enable_rating
            ]

            # Format each category
            char_list = format_tag_items(character_items, excluded, replace_underscore, include_confidence)
            copy_list = format_tag_items(copyright_items, excluded, replace_underscore, include_confidence)
            style_list = format_tag_items(style_items, excluded, replace_underscore, include_confidence)
            cloth_list = format_tag_items(clothing_items, excluded, replace_underscore, include_confidence)
            gen_list = format_tag_items(general_items, excluded, replace_underscore, include_confidence)
            meta_list = format_tag_items(meta_items, excluded, replace_underscore, include_confidence)
            rating_list = format_tag_items(rating_items, excluded, replace_underscore, include_confidence)

            # Combined prompt in natural Danbooru ordering:
            # character -> copyright -> style -> clothing -> general -> meta -> rating
            combined_elements = []
            for tag_list in (char_list, copy_list, style_list, cloth_list, gen_list, meta_list, rating_list):
                combined_elements.extend(tag_list)

            combined_str = ", ".join(combined_elements)
            all_combined.append(combined_str)
            all_character.append(", ".join(char_list))
            all_copyright.append(", ".join(copy_list))
            all_style.append(", ".join(style_list))
            all_clothing.append(", ".join(cloth_list))
            all_general.append(", ".join(gen_list))
            all_meta.append(", ".join(meta_list))
            all_rating.append(", ".join(rating_list))

            # Store structured details for SwarmUI
            if details_summary is None:
                details_summary = {
                    "character": [{"tag": tag.replace("_", " ") if replace_underscore else tag, "score": round(score, 4)} for tag, score in sorted(character_items, key=lambda x: x[1], reverse=True)],
                    "copyright": [{"tag": tag.replace("_", " ") if replace_underscore else tag, "score": round(score, 4)} for tag, score in sorted(copyright_items, key=lambda x: x[1], reverse=True)],
                    "style": [{"tag": tag.replace("_", " ") if replace_underscore else tag, "score": round(score, 4)} for tag, score in sorted(style_items, key=lambda x: x[1], reverse=True)],
                    "clothing": [{"tag": tag.replace("_", " ") if replace_underscore else tag, "score": round(score, 4)} for tag, score in sorted(clothing_items, key=lambda x: x[1], reverse=True)],
                    "general": [{"tag": tag.replace("_", " ") if replace_underscore else tag, "score": round(score, 4)} for tag, score in sorted(general_items, key=lambda x: x[1], reverse=True)],
                    "meta": [{"tag": tag.replace("_", " ") if replace_underscore else tag, "score": round(score, 4)} for tag, score in sorted(meta_items, key=lambda x: x[1], reverse=True)],
                    "rating": [{"tag": tag.replace("_", " ") if replace_underscore else tag, "score": round(score, 4)} for tag, score in sorted(rating_items, key=lambda x: x[1], reverse=True)],
                }

        # If requested by SwarmUI, write the structured JSON output to file
        if output_path:
            try:
                output_payload = {
                    "success": True,
                    "inference_time_sec": round(inference_time, 3),
                    "combined_tags": all_combined[0] if all_combined else "",
                    "character": all_character[0] if all_character else "",
                    "copyright": all_copyright[0] if all_copyright else "",
                    "style": all_style[0] if all_style else "",
                    "clothing": all_clothing[0] if all_clothing else "",
                    "general": all_general[0] if all_general else "",
                    "meta": all_meta[0] if all_meta else "",
                    "rating": all_rating[0] if all_rating else "",
                    "details": details_summary or {}
                }
                with open(output_path, "w", encoding="utf-8") as f:
                    json.dump(output_payload, f, ensure_ascii=False, indent=2)
            except Exception as exc:
                print(f"[PixAITagger] Error writing output JSON to {output_path}: {exc}")

        res_tuple = (
            all_combined[0] if all_combined else "",
            all_character[0] if all_character else "",
            all_copyright[0] if all_copyright else "",
            all_style[0] if all_style else "",
            all_clothing[0] if all_clothing else "",
            all_general[0] if all_general else "",
            all_meta[0] if all_meta else "",
            all_rating[0] if all_rating else "",
        )
        return {"ui": {"tags": [all_combined[0] if all_combined else ""]}, "result": res_tuple}


NODE_CLASS_MAPPINGS = {
    "PixaiTaggerGenerate": PixaiTaggerGenerate,
}

NODE_DISPLAY_NAME_MAPPINGS = {
    "PixaiTaggerGenerate": "🌸 PixAI Tagger v1.0 (GPU)",
}

__all__ = ["PixaiTaggerGenerate", "NODE_CLASS_MAPPINGS", "NODE_DISPLAY_NAME_MAPPINGS"]
