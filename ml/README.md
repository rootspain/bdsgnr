# Распознавание элементов уступа: обучение модели

Модель классифицирует участки линий маркшейдерской съёмки (верхняя бровка, нижняя бровка, горизонталь, граница, прочее) по признакам `SurveyFeatures` (Engine). Обучается только на синтетике; план и результаты — `doc/onnx-surface-types.md`.

## Окружение

```
python -m venv ml/.venv
ml/.venv/Scripts/python -m pip install -r ml/requirements.txt
```

## Данные

Генерирует утилита `tools/BlastDesigner.SurveyData` (сцена → DXF → импорт программы → признаки и истинный класс, плюс ответ правил):

```
dotnet run --project tools/BlastDesigner.SurveyData -- dataset ml/data/train.csv 1500 train 1
dotnet run --project tools/BlastDesigner.SurveyData -- dataset ml/data/shifted.csv 300 shifted 100001
dotnet run --project tools/BlastDesigner.SurveyData -- dataset ml/data/traps.csv 350 train 200001 --traps
```

`ml/data/` в `.gitignore`. Перед обучением стоит проверить, что синтетика накрывает реальный чертёж: `calibrate <чертёж> 150 train`.

## Обучение

```
ml/.venv/Scripts/python ml/train.py
```

Пишет в `ml/models/`: `survey-segments.onnx` (модель; в метаданных — версия признаков, список признаков, классы), `metrics.json` (модель против правил на проверке, «другой» синтетике и ловушках), `parity.json` (эталонные входы и вероятности для теста совпадения в C#).

`ml/experiment.py [признаки_чертежа.csv]` сравнивает наборы признаков; признаки реального чертежа даёт `features <чертёж> samples/<имя>.features.csv` (только в `samples/`, не в репозиторий).
