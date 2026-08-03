# Third-party notices

## RapidOCR and PP-OCR recognition model

The phase-two text recognizer uses the `PP-OCRv6_rec_small.onnx` model
distributed with RapidOCR 3.9.2. RapidOCR and its model distribution are used
under the Apache License 2.0. The packaged license and exact source/hash
metadata are located in `data/ocr/rapidocr`.

Only the recognition stage is used. CurrencyWarsAssistant independently
locates normalized game UI regions and does not copy RapidOCR's text-detection
or application orchestration code.

Upstream projects:

- https://github.com/RapidAI/RapidOCR
- https://github.com/PaddlePaddle/PaddleOCR

## Microsoft ONNX Runtime

The .NET application uses Microsoft.ML.OnnxRuntime to run the packaged ONNX
model locally. ONNX Runtime is distributed under the MIT License:

- https://github.com/microsoft/onnxruntime
