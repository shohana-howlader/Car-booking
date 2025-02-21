using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Globalization;
using Wafi.SampleTest.Dtos;
using Wafi.SampleTest.Entities;

namespace Wafi.SampleTest.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class BookingsController : ControllerBase
    {
        private readonly WafiDbContext _context;

        public BookingsController(WafiDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public IActionResult GetAll()
        {
            var bookings = _context.Bookings.Include(e => e.Car).ToList();
            return Ok(bookings);
        }

        [HttpGet("GetCar")]
        public IActionResult GetCar()
        {
            var bookings = _context.Cars.ToList();
            return Ok(bookings);
        }




        // GET: api/Bookings
        [HttpGet("Booking")]

        public async Task<ActionResult<IEnumerable<BookingCalendarDto>>> GetCalendarBookings([FromQuery] BookingFilterDto input)
        {
            // Convert DateTime to DateOnly
            var startDate = DateOnly.FromDateTime(input.StartBookingDate);
            var endDate = DateOnly.FromDateTime(input.EndBookingDate);

            var bookings = await _context.Bookings
                .Include(b => b.Car)
                .Where(b => b.CarId == input.CarId)
                .ToListAsync();

            var calendarBookings = new List<BookingCalendarDto>();

            foreach (var booking in bookings)
            {
                var bookingDates = GenerateBookingDates(booking, startDate, endDate);

                foreach (var date in bookingDates)
                {
                    calendarBookings.Add(new BookingCalendarDto
                    {
                        BookingDate = date,
                        StartTime = booking.StartTime,
                        EndTime = booking.EndTime,
                        CarModel = booking.Car.Model
                    });
                }
            }

            return Ok(calendarBookings.OrderBy(b => b.BookingDate).ThenBy(b => b.StartTime));
        }

        private IEnumerable<DateOnly> GenerateBookingDates(Booking booking, DateOnly startDate, DateOnly endDate)
        {
            var dates = new List<DateOnly>();
            var currentDate = booking.BookingDate;

            // If the booking ends before the filter start date or starts after the filter end date
            if ((booking.EndRepeatDate.HasValue && booking.EndRepeatDate.Value < startDate) ||
                (booking.BookingDate > endDate))
            {
                return dates;
            }

            // Adjust the start date if the booking starts later
            if (booking.BookingDate > startDate)
            {
                startDate = booking.BookingDate;
            }

            // Adjust the end date if the booking ends earlier
            if (booking.EndRepeatDate.HasValue && booking.EndRepeatDate.Value < endDate)
            {
                endDate = booking.EndRepeatDate.Value;
            }

            switch (booking.RepeatOption)
            {
                case RepeatOption.DoesNotRepeat:
                    if (booking.BookingDate >= startDate && booking.BookingDate <= endDate)
                    {
                        dates.Add(booking.BookingDate);
                    }
                    break;

                case RepeatOption.Daily:
                    currentDate = startDate;
                    while (currentDate <= endDate)
                    {
                        if (currentDate >= booking.BookingDate)
                        {
                            dates.Add(currentDate);
                        }
                        currentDate = currentDate.AddDays(1);
                    }
                    break;

                case RepeatOption.Weekly:
                    currentDate = startDate;
                    while (currentDate <= endDate)
                    {
                        if (currentDate >= booking.BookingDate)
                        {
                            if (booking.DaysToRepeatOn == null || booking.DaysToRepeatOn == DaysOfWeek.None)
                            {
                                // If no specific days selected, use the original booking day
                                if (currentDate.DayOfWeek == booking.BookingDate.DayOfWeek)
                                {
                                    dates.Add(currentDate);
                                }
                            }
                            else
                            {
                                // Check if current day is in the selected days
                                var currentDayFlag = (DaysOfWeek)(1 << ((int)currentDate.DayOfWeek));
                                if (booking.DaysToRepeatOn.Value.HasFlag(currentDayFlag))
                                {
                                    dates.Add(currentDate);
                                }
                            }
                        }
                        currentDate = currentDate.AddDays(1);
                    }
                    break;
            }

            return dates;
        }

        // POST: api/Bookings
        [HttpPost("Booking")]
        public async Task<ActionResult<CreateUpdateBookingDto>> PostBooking(CreateUpdateBookingDto booking)
        {
            // Validate basic booking time logic
            if (booking.StartTime >= booking.EndTime)
            {
                // return BadRequest("End time must be after start time");                  
                return BadRequest(new { message = "End time must be after start time" });

            }

            // Generate all booking dates based on repeat options
            var bookingDates = GenerateBookingDates(booking);

            // Check for conflicts with existing bookings
            foreach (var date in bookingDates)
            {
                var conflicts = await CheckForConflicts(date, booking.StartTime, booking.EndTime, booking.CarId, booking.Id);
                if (conflicts)
                {
                    //return BadRequest($"Booking conflicts with existing reservation on {date.ToShortDateString()}");
                    return BadRequest(new { message = $"Booking conflicts with existing reservation on {date.ToShortDateString()}" });

                }
            }

            // Map DTO to entity
            var bookingEntity = new Booking
            {
                Id = booking.Id == Guid.Empty ? Guid.NewGuid() : booking.Id,
                BookingDate = booking.BookingDate,
                StartTime = booking.StartTime,
                EndTime = booking.EndTime,
                RepeatOption = booking.RepeatOption,
                EndRepeatDate = booking.EndRepeatDate,
                DaysToRepeatOn = booking.DaysToRepeatOn,
                RequestedOn = DateTime.UtcNow,
                CarId = booking.CarId
            };

            await _context.Bookings.AddAsync(bookingEntity);
            await _context.SaveChangesAsync();

            return Ok(booking);
        }

        private IEnumerable<DateOnly> GenerateBookingDates(CreateUpdateBookingDto booking)
        {
            var dates = new List<DateOnly>();
            var currentDate = booking.BookingDate;

            switch (booking.RepeatOption)
            {
                case RepeatOption.DoesNotRepeat:
                    dates.Add(currentDate);
                    break;

                case RepeatOption.Daily:
                    while (currentDate <= (booking.EndRepeatDate ?? currentDate))
                    {
                        dates.Add(currentDate);
                        currentDate = currentDate.AddDays(1);
                    }
                    break;

                case RepeatOption.Weekly:
                    while (currentDate <= (booking.EndRepeatDate ?? currentDate))
                    {
                        if (booking.DaysToRepeatOn == null || booking.DaysToRepeatOn == DaysOfWeek.None)
                        {
                            // If no specific days selected, use the original booking day
                            dates.Add(currentDate);
                            currentDate = currentDate.AddDays(7);
                        }
                        else
                        {
                            // Check each day of the week
                            for (int i = 0; i < 7; i++)
                            {
                                var dayOfWeek = (DaysOfWeek)(1 << ((int)currentDate.AddDays(i).DayOfWeek));
                                if (booking.DaysToRepeatOn.Value.HasFlag(dayOfWeek))
                                {
                                    dates.Add(currentDate.AddDays(i));
                                }
                            }
                            currentDate = currentDate.AddDays(7);
                        }
                    }
                    break;
            }

            return dates;
        }

        private async Task<bool> CheckForConflicts(DateOnly date, TimeSpan startTime, TimeSpan endTime, Guid carId, Guid bookingId)
        {
            // Find any bookings for the same car on the same date
            var existingBookings = await _context.Bookings
                .Where(b => b.CarId == carId && b.Id != bookingId)
                .ToListAsync();

            foreach (var existingBooking in existingBookings)
            {
                // Generate all dates for the existing booking
                var existingBookingDates = GenerateBookingDates(new CreateUpdateBookingDto
                {
                    BookingDate = existingBooking.BookingDate,
                    EndRepeatDate = existingBooking.EndRepeatDate,
                    RepeatOption = existingBooking.RepeatOption,
                    DaysToRepeatOn = existingBooking.DaysToRepeatOn
                });

                // Check if the date we're checking falls on any of the existing booking's dates
                if (existingBookingDates.Contains(date))
                {
                    // Check for time overlap
                    if (startTime < existingBooking.EndTime && endTime > existingBooking.StartTime)
                    {
                        return true; // Conflict found
                    }
                }
            }

            return false; // No conflicts
        }

        // GET: api/SeedData
        // For test purpose
        [HttpGet("SeedData")]
        public async Task<IEnumerable<BookingCalendarDto>> GetSeedData()
        {
            var cars = await _context.Cars.ToListAsync();

            if (!cars.Any())
            {
                cars = GetCars().ToList();
                await _context.Cars.AddRangeAsync(cars);
                await _context.SaveChangesAsync();
            }

            var bookings = await _context.Bookings.ToListAsync();

            if(!bookings.Any())
            {
                bookings = GetBookings().ToList();

                await _context.Bookings.AddRangeAsync(bookings);
                await _context.SaveChangesAsync();
            }

            var calendar = new Dictionary<DateOnly, List<Booking>>();

            foreach (var booking in bookings)
            {
                var currentDate = booking.BookingDate;
                while (currentDate <= (booking.EndRepeatDate ?? booking.BookingDate))
                {
                    if (!calendar.ContainsKey(currentDate))
                        calendar[currentDate] = new List<Booking>();

                    calendar[currentDate].Add(booking);

                    currentDate = booking.RepeatOption switch
                    {
                        RepeatOption.Daily => currentDate.AddDays(1),
                        RepeatOption.Weekly => currentDate.AddDays(7),
                        _ => booking.EndRepeatDate.HasValue ? booking.EndRepeatDate.Value.AddDays(1) : currentDate.AddDays(1)
                    };
                }
            }

            List<BookingCalendarDto> result = new List<BookingCalendarDto>();

            foreach (var item in calendar)
            {
                foreach(var booking in item.Value)
                {
                    result.Add(new BookingCalendarDto { BookingDate = booking.BookingDate, CarModel = booking.Car.Model, StartTime = booking.StartTime, EndTime = booking.EndTime });
                }
            }

            return result;
        }

        #region Sample Data

        private IList<Car> GetCars()
        {
            var cars = new List<Car>
            {
                new Car { Id = Guid.NewGuid(), Make = "Toyota", Model = "Corolla" },
                new Car { Id = Guid.NewGuid(), Make = "Honda", Model = "Civic" },
                new Car { Id = Guid.NewGuid(), Make = "Ford", Model = "Focus" }
            };

            return cars;
        }

        private IList<Booking> GetBookings()
        {
            var cars = GetCars();

            var bookings = new List<Booking>
            {
                new Booking { Id = Guid.NewGuid(), BookingDate = new DateOnly(2025, 2, 5), StartTime = new TimeSpan(10, 0, 0), EndTime = new TimeSpan(12, 0, 0), RepeatOption = RepeatOption.DoesNotRepeat, RequestedOn = DateTime.Now, CarId = cars[0].Id, Car = cars[0] },
                new Booking { Id = Guid.NewGuid(), BookingDate = new DateOnly(2025, 2, 10), StartTime = new TimeSpan(14, 0, 0), EndTime = new TimeSpan(16, 0, 0), RepeatOption = RepeatOption.Daily, EndRepeatDate = new DateOnly(2025, 2, 20), RequestedOn = DateTime.Now, CarId = cars[1].Id, Car = cars[1] },
                new Booking { Id = Guid.NewGuid(), BookingDate = new DateOnly(2025, 2, 15), StartTime = new TimeSpan(9, 0, 0), EndTime = new TimeSpan(10, 30, 0), RepeatOption = RepeatOption.Weekly, EndRepeatDate = new DateOnly(2025, 3, 31), RequestedOn = DateTime.Now, DaysToRepeatOn = DaysOfWeek.Monday, CarId = cars[2].Id,  Car = cars[2] },
                new Booking { Id = Guid.NewGuid(), BookingDate = new DateOnly(2025, 3, 1), StartTime = new TimeSpan(11, 0, 0), EndTime = new TimeSpan(13, 0, 0), RepeatOption = RepeatOption.DoesNotRepeat, RequestedOn = DateTime.Now, CarId = cars[0].Id, Car = cars[0] },
                new Booking { Id = Guid.NewGuid(), BookingDate = new DateOnly(2025, 3, 7), StartTime = new TimeSpan(8, 0, 0), EndTime = new TimeSpan(10, 0, 0), RepeatOption = RepeatOption.Weekly, EndRepeatDate = new DateOnly(2025, 3, 28), RequestedOn = DateTime.Now, DaysToRepeatOn = DaysOfWeek.Friday, CarId = cars[1].Id, Car = cars[1] },
                new Booking { Id = Guid.NewGuid(), BookingDate = new DateOnly(2025, 3, 15), StartTime = new TimeSpan(15, 0, 0), EndTime = new TimeSpan(17, 0, 0), RepeatOption = RepeatOption.Daily, EndRepeatDate = new DateOnly(2025, 3, 20), RequestedOn = DateTime.Now, CarId = cars[2].Id,  Car = cars[2] }
            };

            return bookings;
        }

            #endregion

        }
}
