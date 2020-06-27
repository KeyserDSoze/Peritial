using Microsoft.AspNetCore.Mvc.ModelBinding;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Expertiser.Database.Binders
{
    public class DecimalModelBinder : IModelBinder
    {
        public Task BindModelAsync(ModelBindingContext bindingContext)
        {
            var valueProviderResult = bindingContext
                .ValueProvider
                .GetValue(bindingContext.ModelName);
            string value = valueProviderResult.FirstValue;
            if (value == null)
            {
                bindingContext.Result = ModelBindingResult.Success(0);
            }
            else
            {
                if (value.Contains(","))
                    value = value.Replace(",", ".");

                decimal.TryParse(
                    value,
                    NumberStyles.AllowDecimalPoint,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var model);

                bindingContext
                    .ModelState
                    .SetModelValue(bindingContext.ModelName, valueProviderResult);

                bindingContext.Result = ModelBindingResult.Success(model);
            }
            return Task.CompletedTask;
        }
    }
}
