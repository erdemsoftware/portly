New UI strings waiting for translation, one file per feature group: `<group>.json` with `{"en": {...}, "tr": {...}}`.
They are merged into `src/Portly/lang/*.json` (all languages) and this folder is emptied before a release.
Until then `I18n.T` shows the English text from `en.json`, or the key itself.
