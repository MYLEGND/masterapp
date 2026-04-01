using Microsoft.AspNetCore.Mvc;
using LegendApp.Models.Budget;
using LegendApp.Services.Budget.Interfaces;

namespace LegendApp.BudgetControllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class NetWorthController : ControllerBase
    {
        private readonly IBudgetCalculator _calculator;

        public NetWorthController(IBudgetCalculator calculator)
        {
            _calculator = calculator;
        }

        [HttpPost("calculate")]
        public IActionResult CalculateNetWorth([FromBody] NetWorthInput input)
        {
            if (input.TotalAssets < 0 || input.TotalLiabilities < 0)
                return BadRequest("Assets and Liabilities must be 0 or higher.");

            var netWorth = _calculator.CalculateNetWorth(input.TotalAssets, input.TotalLiabilities);

            return Ok(new { NetWorth = netWorth });
        }
    }
}
