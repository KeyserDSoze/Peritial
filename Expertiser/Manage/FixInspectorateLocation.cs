using Expertiser.Database.Models;
using Microsoft.EntityFrameworkCore;
using Rystem.UnitTest;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Expertiser.Manage
{
    public class FixInspectorateLocation : IUnitTest
    {
        public async Task DoWorkAsync(Action<object> action, UnitTestMetrics metrics, params string[] args)
        {
            using (ContextExpertiser context = new ContextExpertiser())
            {
                List<TableCity> cities = await context.TableCity.ToListAsync();
                List<TableLocation> locations = await context.TableLocation.Include(x => x.City).ToListAsync();
                foreach (TableInspectorate inspectorate in await context.TableInspectorate.ToListAsync())
                {
                    bool checkin = false;
                    foreach (TableCity city in cities)
                    {
                        if (city.Name == "Vittoria")
                            continue;
                        if (inspectorate.Label.ToLower().Contains($" {city.Name.ToLower()} ") ||
                            inspectorate.Label.ToLower().Contains($" {city.Name.ToLower()}-") ||
                            inspectorate.Label.ToLower().Contains($"-{city.Name.ToLower()} ") ||
                            inspectorate.Label.ToLower().Contains($"-{city.Name.ToLower()}-") ||
                            inspectorate.Label.ToLower().StartsWith($"{city.Name.ToLower()} ") ||
                            inspectorate.Label.ToLower().StartsWith($"{city.Name.ToLower()}-") ||
                            inspectorate.Label.ToLower().Contains($"({city.Name.ToLower()})")
                            )
                        {
                            string oldLabel = inspectorate.Label;
                            TableLocation location = locations.Where(x => x.CityId == city.Id).FirstOrDefault(x => string.IsNullOrWhiteSpace(x.Address));
                            if (location == null)
                            {
                                location = new TableLocation()
                                {
                                    Address = string.Empty,
                                    CityId = city.Id,
                                    PostalCode = city.PostalCodes.First(),
                                };
                                context.TableLocation.Add(location);
                                await context.SaveChangesAsync();
                                locations.Add(location);
                            }
                            inspectorate.AddressId = location.Id;
                            context.TableInspectorate.Update(inspectorate);
                            await context.SaveChangesAsync();
                            Console.WriteLine($"{oldLabel} --> New Record in {city.Name}");
                            checkin = true;
                            break;
                        }
                    }
                    if (!checkin)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"{inspectorate.Label}");
                        Console.ResetColor();
                    }
                }
                await context.SaveChangesAsync();
            }
        }
    }
}
