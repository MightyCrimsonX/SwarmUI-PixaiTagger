"""PixAI Tagger v1.0 custom nodes for ComfyUI."""

from .PixaiTaggerNode import PixaiTaggerGenerate

NODE_CLASS_MAPPINGS = {
    "PixaiTaggerGenerate": PixaiTaggerGenerate,
}

NODE_DISPLAY_NAME_MAPPINGS = {
    "PixaiTaggerGenerate": "🌸 PixAI Tagger v1.0 (GPU)",
}

__all__ = ["NODE_CLASS_MAPPINGS", "NODE_DISPLAY_NAME_MAPPINGS"]
