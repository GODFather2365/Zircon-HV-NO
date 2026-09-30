# Zircon-HV-NO (Zircon Heavy Variant)

Мод для Nuclear Option 0.34.2 (Unity 2022.3.62f2, BepInEx 5.4.23.4 Mono, Blueprinter 2.0.1).

## Задача
Отдельный вариант ракеты «Циркон» на базе префаба `zircon nuke` из мода
Multi-Missile (kingwixly): рантайм-клон с уникальным ID `Zircon_HV_GODFather`,
ядерная БЧ настраиваемой мощности (BepInEx-конфиг YieldKt,
по умолчанию 1000 = 1 Мт; можно 500).

## Требования
- Без Unity Editor и без своего .nobp: всё в рантайме (Harmony + reflection).
- Регистрация клона в Blueprinter 2.0.1 как ОТДЕЛЬНОГО оружия,
  уникальные jsonKey/ID, не конфликтовать с оригиналом и чужими модами.
- Если Multi-Missile не найден — логируемся и отключаемся, не падаем.
- Установка: DLL в BepInEx/plugins (NOMM — как ручная установка).
- Этап 2 (задел): рантайм-подмена меша клона на внешний .obj из папки мода
  (свой OBJ-парсер, материал URP/Lit) — сейчас достаточно точки расширения.

## Проверенный контекст (из Player.log)
- BundleRegistry грузит .nobp из resource:<Assembly>:<path>.nobp и из plugins/**
- В бандле Multi-Missile 1.3.1 есть
  `Assets/Blueprinter/Mods/Multi - Missile/zircon nuke/zircon nuke.prefab`
- Референсы рантайм-техник: Tsar Bomba (клон ванильной БЧ, Standard→URP/Lit,
  Encyclopedia-патч), YJ-18/AGM84H/PL-15 (рефлексия к Blueprinter),
  YJ20ASBM (замена компонентов префаба), Meridian Works (EventGate.BuildTwins)
- Kestrel-40 умер от TypeLoadException Blueprinter.PatchRunner —
  не повторять его ошибок совместимости версий

## Сборка
DLL собирается в окружении Qwen Coder: dotnet CLI + NuGet
(BepInEx.Core 5.4.23.x, Lib.HarmonyX, UnityEngine.Modules 2022.3.62).
Готовую DLL положить в releases репозитория.

## Что выдать
1. План и риски по регистрации в Blueprinter.
2. Полный исходник (csproj + cs).
3. Собрать DLL и закоммитить в releases.
4. Инструкцию по установке и настройке YieldKt.
5. Чек-лист проверки в игре и типичные ошибки.
