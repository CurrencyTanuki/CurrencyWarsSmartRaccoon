import json
d = json.load(open('data/4.4/currency-wars-characters.json', encoding='utf-8'))
def walk(o, path=''):
    if isinstance(o, dict):
        for k, v in o.items():
            walk(v, path + '.' + str(k))
    elif isinstance(o, list):
        for i, v in enumerate(o):
            walk(v, path + f'[{i}]')
    elif isinstance(o, str):
        if any(c in o for c in '霍藿'):
            print(f'{path}: {o}')
walk(d)
