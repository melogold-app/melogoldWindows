"""Собирает Strings/{ru-RU,en-US}/Resources.resw из tools/strings.tsv: ключ<TAB>русский<TAB>английский.
Тексты — по docs/GLOSSARY.md Android. Запуск: python tools/strings.py"""
import os, html
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
rows = []
with open(os.path.join(root, 'tools', 'strings.tsv'), encoding='utf-8') as f:
    for n, line in enumerate(f, 1):
        line = line.rstrip('\n')
        if not line or line.startswith('#'):
            continue
        parts = line.split('\t')
        if len(parts) != 3:
            raise SystemExit(f'strings.tsv:{n}: нужно 3 колонки, а их {len(parts)}')
        rows.append(parts)
keys = [r[0] for r in rows]
dup = {k for k in keys if keys.count(k) > 1}
if dup:
    raise SystemExit(f'повторы ключей: {sorted(dup)}')
head = '''<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
'''
for lang, col in (('ru-RU', 1), ('en-US', 2)):
    out = [head]
    for r in rows:
        value = r[col].replace('\n', '\n')
        out.append(f'  <data name="{html.escape(r[0])}" xml:space="preserve"><value>{html.escape(value, quote=False)}</value></data>\n')
    out.append('</root>\n')
    path = os.path.join(root, 'src', 'Melogold.App', 'Strings', lang, 'Resources.resw')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w', encoding='utf-8', newline='\r\n') as f:
        f.write(''.join(out))
print(f'{len(rows)} strings')
