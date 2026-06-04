using Dollab_Backend.Data;
using Dollab_Backend.Services;
using Dollab_Backend.Helpers;
using Dollab_Backend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Dollab_Backend.Controllers
{
    [ApiController]
    [Route("api/blocked-users")]
    [Authorize]
    public class BlockedUsersController : ControllerBase
    {
        private readonly AppDbContext _context;

        public BlockedUsersController(AppDbContext context)
        {
            _context = context;
        }

        [HttpPost("{userId}")]
        public async Task<IActionResult> BlockUser(int userId)
        {
            var currentUserId = User.GetUserId();

            if (currentUserId == userId)
                return BadRequest("Нельзя заблокировать самого себя");

            var userExists = await _context.Users.AnyAsync(u => u.Id == userId);

            if (!userExists)
                return NotFound("Пользователь не найден");

            var alreadyBlocked = await _context.BlockedUsers
                .AnyAsync(b => b.BlockerId == currentUserId && b.BlockedId == userId);

            if (alreadyBlocked)
                return BadRequest("Пользователь уже заблокирован");

            var blockedUser = new BlockedUser
            {
                BlockerId = currentUserId,
                BlockedId = userId,
                CreatedAt = DateTime.UtcNow
            };

            _context.BlockedUsers.Add(blockedUser);

            var follows = await _context.Follows
                .Where(f =>
                    (f.FollowerId == currentUserId && f.FollowingId == userId) ||
                    (f.FollowerId == userId && f.FollowingId == currentUserId))
                .ToListAsync();

            _context.Follows.RemoveRange(follows);

            var requests = await _context.FollowRequests
                .Where(r =>
                    r.Status == "pending" &&
                    (
                        (r.RequesterId == currentUserId && r.TargetUserId == userId) ||
                        (r.RequesterId == userId && r.TargetUserId == currentUserId)
                    ))
                .ToListAsync();

            _context.FollowRequests.RemoveRange(requests);

            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = "Пользователь добавлен в черный список"
            });
        }

        [HttpDelete("{userId}")]
        public async Task<IActionResult> UnblockUser(int userId)
        {
            var currentUserId = User.GetUserId();

            var block = await _context.BlockedUsers
                .FirstOrDefaultAsync(b =>
                    b.BlockerId == currentUserId &&
                    b.BlockedId == userId);

            if (block == null)
                return NotFound("Пользователь не найден в черном списке");

            _context.BlockedUsers.Remove(block);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = "Пользователь удален из черного списка"
            });
        }

        [HttpGet]
        public async Task<IActionResult> GetBlockedUsers()
        {
            var currentUserId = User.GetUserId();

            var users = await _context.BlockedUsers
                .Where(b => b.BlockerId == currentUserId)
                .Select(b => new
                {
                    id = b.Blocked.Id,
                    username = b.Blocked.Username,
                    avatarUrl = b.Blocked.AvatarUrl,
                    blockedAt = b.CreatedAt
                })
                .ToListAsync();

            return Ok(users);
        }
    }
}