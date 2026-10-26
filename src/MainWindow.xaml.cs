private static double? ExtractParameterValue(JsonElement root, params string[] targetNames)
        {
            // 1. Проверяем прямые свойства в корне JSON объекта
            foreach (var name in targetNames)
            {
                if (root.TryGetProperty(name, out var prop))
                {
                    if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out double val))
                        return val;
                    if (prop.ValueKind == JsonValueKind.String && double.TryParse(prop.GetString(), out double sVal))
                        return sVal;
                }
            }

            // 2. Проверяем массив "values" или любые другие массивы/коллекции во вложенности
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in prop.Value.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Object)
                            {
                                // Ищем имя или тип параметра
                                string typeName = "";
                                if (item.TryGetProperty("type", out var tProp)) typeName = tProp.GetString() ?? "";
                                if (string.IsNullOrEmpty(typeName) && item.TryGetProperty("parameterType", out var ptProp)) typeName = ptProp.GetString() ?? "";
                                if (string.IsNullOrEmpty(typeName) && item.TryGetProperty("name", out var nProp)) typeName = nProp.GetString() ?? "";
                                if (string.IsNullOrEmpty(typeName) && item.TryGetProperty("title", out var titProp)) typeName = titProp.GetString() ?? "";
                                if (string.IsNullOrEmpty(typeName) && item.TryGetProperty("parameter", out var pProp)) typeName = pProp.ToString();

                                // Также в ЛЭРС параметр может быть идентифицирован по цифровому ID или коду
                                bool matchesName = targetNames.Any(tn => typeName.IndexOf(tn, StringComparison.OrdinalIgnoreCase) >= 0);

                                // Если совпало по имени/типу ИЛИ это массив values — смотрим само значение
                                if (matchesName || prop.Name.Equals("values", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Пробуем достать число из свойства value, val или currentValue
                                    if (item.TryGetProperty("value", out var valProp) || 
                                        item.TryGetProperty("val", out valProp) || 
                                        item.TryGetProperty("currentValue", out valProp))
                                    {
                                        // Если совпало конкретное имя параметра или в values нет фильтра по имени
                                        if (matchesName || targetNames.Length == 0)
                                        {
                                            if (valProp.ValueKind == JsonValueKind.Number && valProp.TryGetDouble(out double v)) return v;
                                            if (valProp.ValueKind == JsonValueKind.String && double.TryParse(valProp.GetString(), out double sv)) return sv;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return null;
        }
