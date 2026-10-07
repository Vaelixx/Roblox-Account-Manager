#!/usr/bin/env python3
"""
Checks the UI translations in src/Localization against the code.

  * every key the XAML ({DynamicResource Str.X}) or C# (L.T / L.N / key literals) uses exists in en.json
  * every other language has exactly the English keys (none missing, none extra)
  * placeholders ({0}, {1:N0}, ...) match the English text and every pattern is a valid .NET format string
  * count-based keys (L.N) have the plural forms each language needs (ru/pl: One/Few/Many)

Exit code 1 on any error, so the release workflow stops before a broken build ships.
Usage: python tools/check-localization.py [--unused]
"""
import glob
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, 'src')
LOC = os.path.join(SRC, 'Localization')

PLURAL_FORMS = {'ru': ['One', 'Few', 'Many'], 'pl': ['One', 'Few', 'Many']}
DEFAULT_FORMS = ['One', 'Other']

errors = []
warnings = []


def read(path):
    with open(path, encoding='utf-8') as f:
        return f.read()


def load_table(code):
    with open(os.path.join(LOC, code + '.json'), encoding='utf-8-sig') as f:
        data = json.load(f)
    return {k: v for k, v in data.items() if not k.startswith('_')}


def source_files(pattern):
    return [p for p in glob.glob(os.path.join(SRC, '**', pattern), recursive=True)
            if os.sep + 'obj' + os.sep not in p and os.sep + 'bin' + os.sep not in p]


# ---------------------------------------------------------------- keys used by the code

xaml_keys = set()
for path in source_files('*.xaml'):
    xaml_keys.update(re.findall(r'\{DynamicResource Str\.([A-Za-z0-9_.]+)\}', read(path)))

def strip_comments(text):
    # Whole-line // and /// comments only: a "//" inside a string (URLs) must survive.
    return '\n'.join(line for line in text.split('\n') if not line.lstrip().startswith('//'))


cs_text = {path: strip_comments(read(path)) for path in source_files('*.cs')}
direct = set()
plural_bases = set()
KEY = r'[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)*'
for text in cs_text.values():
    for fn, key in re.findall(r'\bL\.([TN])\(\s*"(' + KEY + r')"', text):
        (plural_bases if fn == 'N' else direct).add(key)
    # ternaries: L.N(cond ? "A" : "B", n) and L.T(cond ? "A" : "B")
    for fn, body in re.findall(r'\bL\.([TN])\(([^;]*?\?\s*"' + KEY + r'"\s*:\s*"' + KEY + r'")', text):
        for key in re.findall(r'"(' + KEY + r')"', body):
            (plural_bases if fn == 'N' else direct).add(key)
    # keys handed to a helper that counts: Report(count, "Key")
    plural_bases.update(re.findall(r'\bReport\([^;]*?,\s*"(' + KEY + r')"\)', text))

    # resources looked up from code: FindResource("Str.Key")
    direct.update(re.findall(r'"Str\.(' + KEY + r')"', text))

prefixes = {k.split('.')[0] for k in xaml_keys | direct | plural_bases}
literal_keys = set()
for text in cs_text.values():
    for key in re.findall(r'"([A-Z][A-Za-z0-9]*(?:\.[A-Za-z0-9_]+)+)"', text):
        # file names ("Updater.exe") end in a lower-case extension and DevTools methods
        # ("Browser.getVersion") have a lower-case segment; key segments are always capitalised
        if key.split('.')[0] in prefixes and not re.search(r'\.[a-z]', key):
            literal_keys.add(key)


def literal_list(path, pattern):
    m = re.search(pattern, read(os.path.join(SRC, path)), re.S)
    return re.findall(r'"([A-Za-z0-9]+)"', m.group(1)) if m else []


dynamic = set()
categories = re.findall(r'Key = "([A-Za-z]+)", IconKey', read(os.path.join(SRC, 'ViewModels', 'SettingsViewModel.cs')))
for c in categories:
    dynamic.update({f'Settings.Cat.{c}', f'Settings.Cat.{c}.Keywords'})
for key in re.findall(r'\("([A-Za-z]+)", "[A-Za-z]+"\)', read(os.path.join(SRC, 'Services', 'ThemeService.cs'))):
    dynamic.add(f'Theme.Key.{key}')
for name in literal_list(os.path.join('Services', 'ThemeService.cs'), r'AccentNames\s*=\s*\{(.*?)\}'):
    dynamic.add(f'Theme.Accent.{name}')
for action in literal_list(os.path.join('Models', 'HotkeyBinding.cs'), r'Actions\s*=\s*\{(.*?)\}'):
    dynamic.add(f'Hotkey.Action.{action}')
dynamic.update(f'Graphics.Texture.{i}' for i in range(4))

used_single = (xaml_keys | direct | literal_keys | dynamic) - plural_bases

# ---------------------------------------------------------------- English

en = load_table('en')

for key in sorted(used_single):
    if key in en:
        continue
    if f'{key}.One' in en or f'{key}.Other' in en:
        continue  # a plural base referenced through a variable
    errors.append(f'en: missing key "{key}"')

for base in sorted(plural_bases):
    for form in DEFAULT_FORMS:
        if f'{base}.{form}' not in en:
            errors.append(f'en: missing plural "{base}.{form}"')

PLACEHOLDER = re.compile(r'(?<!\{)\{(\d+)(?:[,:][^}]*)?\}(?!\})')


def placeholders(text):
    return sorted(set(int(n) for n in PLACEHOLDER.findall(text)))


def valid_format(text):
    stripped = text.replace('{{', '').replace('}}', '')
    stripped = PLACEHOLDER.sub('', stripped)
    return '{' not in stripped and '}' not in stripped


for key, text in en.items():
    if not valid_format(text):
        errors.append(f'en: "{key}" is not a valid format string: {text!r}')

if '--unused' in sys.argv:
    plural_keys = {f'{b}.{f}' for b in plural_bases for f in ['One', 'Few', 'Many', 'Other']}
    for key in sorted(en):
        if key not in used_single and key not in plural_keys:
            warnings.append(f'en: key "{key}" looks unused')


def plural_base_of(key):
    for form in ('One', 'Few', 'Many', 'Other'):
        if key.endswith('.' + form) and key[: -len(form) - 1] in plural_bases:
            return key[: -len(form) - 1], form
    return None, None


# ---------------------------------------------------------------- translations

for path in sorted(glob.glob(os.path.join(LOC, '*.json'))):
    code = os.path.splitext(os.path.basename(path))[0]
    if code == 'en':
        continue
    try:
        table = load_table(code)
    except json.JSONDecodeError as e:
        errors.append(f'{code}: invalid JSON: {e}')
        continue

    forms = PLURAL_FORMS.get(code, DEFAULT_FORMS)
    expected = set()
    for key in en:
        base, _ = plural_base_of(key)
        if base is None:
            expected.add(key)
    for base in plural_bases:
        expected.update(f'{base}.{f}' for f in forms)

    for key in sorted(expected - set(table)):
        errors.append(f'{code}: missing "{key}"')
    for key in sorted(set(table) - expected):
        errors.append(f'{code}: unexpected key "{key}"')

    for key, text in table.items():
        if not isinstance(text, str) or not text.strip():
            errors.append(f'{code}: "{key}" is empty')
            continue
        if not valid_format(text):
            errors.append(f'{code}: "{key}" is not a valid format string: {text!r}')
            continue
        base, _ = plural_base_of(key)
        english = en.get(f'{base}.Other') if base else en.get(key)
        if english is None:
            continue
        want, got = placeholders(english), placeholders(text)
        # A plural form may drop {0} (e.g. "one account" spelled out); it must not invent new ones.
        if base:
            if not set(got) <= set(want):
                errors.append(f'{code}: "{key}" placeholders {got} not in English {want}')
        elif want != got:
            errors.append(f'{code}: "{key}" placeholders {got} differ from English {want}')

for w in warnings:
    print('warning:', w)
for e in errors:
    print('error:', e)
print(f'{len(en)} English keys, {len(used_single) + len(plural_bases)} referenced, {len(errors)} error(s)')
sys.exit(1 if errors else 0)
