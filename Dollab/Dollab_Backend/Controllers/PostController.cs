using Dollab_Backend.Data;
using Dollab_Backend.DTOs;
using Dollab_Backend.Helpers;
using Dollab_Backend.Models;
using Dollab_Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

[ApiController]
[Route("api/[controller]")]
public class PostController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly NotificationService _notificationService;


    public PostController(AppDbContext context, NotificationService notificationService)
    {
        _context = context;
        _notificationService = notificationService;
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> CreatePost([FromForm] CreatePostDto dto)
    {
        var userId = User.GetUserId();

        if (dto.Image == null)
            return BadRequest("Image is required");

        var folder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot/posts");

        if (!Directory.Exists(folder))
            Directory.CreateDirectory(folder);

        var fileName = $"{Guid.NewGuid()}{Path.GetExtension(dto.Image.FileName)}";
        var filePath = Path.Combine(folder, fileName);

        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await dto.Image.CopyToAsync(stream);
        }

        var post = new Post
        {
            ImageUrl = $"/posts/{fileName}",
            Description = dto.Description,
            UserId = userId
        };

        _context.Posts.Add(post);
        await _context.SaveChangesAsync();
        var followerIds = await _context.Follows
            .Where(f => f.FollowingId == userId)
            .Select(f => f.FollowerId)
            .ToListAsync();

        foreach (var followerId in followerIds)
        {
            await _notificationService.CreateNotificationAsync(
                userId: followerId,
                fromUserId: userId,
                type: NotificationType.NewPost,
                message: "добавил новый пост",
                postId: post.Id
            );
        }
        var mentionedUsers = await GetMentionedUsersAsync(post.Description);

        foreach (var mentionedUser in mentionedUsers)
        {
            if (mentionedUser.Id == userId)
                continue;

            await _notificationService.CreateNotificationAsync(
                userId: mentionedUser.Id,
                fromUserId: userId,
                type: NotificationType.Mention,
                message: "упомянул вас в посте",
                postId: post.Id
            );
        }
        return Ok(new
        {
            message = "Post created",
            post.Id,
            post.ImageUrl,
            post.Description,
            post.CreatedAt
        });
    }
    [HttpPatch("{id}")]
    [Authorize]
    public async Task<IActionResult> UpdatePost(int id, [FromForm] UpdatePostDto dto)
    {
        var userId = User.GetUserId();

        var post = await _context.Posts.FirstOrDefaultAsync(p => p.Id == id);

        if (post == null)
            return NotFound();

        if (post.UserId != userId)
            return Forbid();

        if (dto.Description != null)
        {
            if (dto.Description.Length > 500)
                return BadRequest("Description too long");

            post.Description = dto.Description;
        }

        if (dto.Image != null)
        {
            var folder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot/posts");

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            var fileName = $"{Guid.NewGuid()}{Path.GetExtension(dto.Image.FileName)}";
            var filePath = Path.Combine(folder, fileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await dto.Image.CopyToAsync(stream);
            }

            post.ImageUrl = $"/posts/{fileName}";
        }

        await _context.SaveChangesAsync();
        var mentionedUsers = await GetMentionedUsersAsync(post.Description);

        foreach (var mentionedUser in mentionedUsers)
        {
            if (mentionedUser.Id == userId)
                continue;

            await _notificationService.CreateNotificationAsync(
                userId: mentionedUser.Id,
                fromUserId: userId,
                type: NotificationType.Mention,
                message: "упомянул вас в посте",
                postId: post.Id
            );
        }
        return Ok(new
        {
            message = "Post updated",
            post.Id,
            post.ImageUrl,
            post.Description,
            post.CreatedAt
        });
    }
    [HttpDelete("{id}")]
    [Authorize]
    public async Task<IActionResult> DeletePost(int id)
    {
        var userId = User.GetUserId();

        var post = await _context.Posts
            .FirstOrDefaultAsync(p => p.Id == id);

        if (post == null)
            return NotFound();

        if (post.UserId != userId)
            return Forbid();

        // 1. Удаляем жалобы на пост
        await _context.Reports
            .Where(r => r.ReportedPostId == id)
            .ExecuteDeleteAsync();

        // 2. Получаем id комментариев поста
        var commentIds = await _context.Comments
            .Where(c => c.PostId == id)
            .Select(c => c.Id)
            .ToListAsync();

        // 3. Удаляем лайки комментариев
        if (commentIds.Any())
        {
            await _context.CommentLikes
                .Where(l => commentIds.Contains(l.CommentId))
                .ExecuteDeleteAsync();
        }

        // 4. Удаляем комментарии
        await _context.Comments
            .Where(c => c.PostId == id)
            .ExecuteDeleteAsync();

        // 5. Удаляем лайки поста
        await _context.Likes
            .Where(l => l.PostId == id)
            .ExecuteDeleteAsync();

        // 6. Удаляем избранное
        await _context.Favorites
            .Where(f => f.PostId == id)
            .ExecuteDeleteAsync();

        // 7. Удаляем сам пост
        _context.Posts.Remove(post);

        await _context.SaveChangesAsync();

        return Ok(new { message = "Post deleted" });
    }
    [HttpGet("my")]
    [Authorize]
    public async Task<IActionResult> GetMyPosts()
    {
        var userId = User.GetUserId();

        var posts = await _context.Posts
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new
            {
                p.Id,
                p.ImageUrl,
                p.Description,
                p.CreatedAt,
                LikesCount = p.Likes.Count,
                IsLiked = p.Likes.Any(l => l.UserId == userId),
                IsHidden = p.IsHidden,
                HiddenReason = p.HiddenReason
            })
            .ToListAsync();

        return Ok(posts);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetPostById(int id)
    {
        int? currentUserId = null;

        if (User.Identity?.IsAuthenticated == true)
        {
            currentUserId = User.GetUserId();
        }

        var post = await _context.Posts
            .Include(p => p.User)
            .Include(p => p.Likes)
            .Include(p => p.Favorites)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (post == null)
            return NotFound();

        return Ok(new
        {
            post.Id,
            post.ImageUrl,
            post.Description,
            post.CreatedAt,

            LikesCount = post.Likes.Count,
            IsLiked = currentUserId != null && post.Likes.Any(l => l.UserId == currentUserId),
            IsFavorited = currentUserId != null && post.Favorites.Any(f => f.UserId == currentUserId),

            Author = new
            {
                Id = post.User.Id,
                Username = post.User.Username,
                AvatarUrl = post.User.AvatarUrl ?? ""
            }
        });
    }

    [HttpPost("{id}/like")]
    [Authorize]
    public async Task<IActionResult> ToggleLike(int id)
    {
        var userId = User.GetUserId();

        var post = await _context.Posts.FindAsync(id);
        if (post == null)
            return NotFound();

        var existingLike = await _context.Likes
            .FirstOrDefaultAsync(l => l.PostId == id && l.UserId == userId);

        bool liked;

        if (existingLike != null)
        {
            _context.Likes.Remove(existingLike);
            liked = false;
        }
        else
        {
            var like = new Like
            {
                PostId = id,
                UserId = userId
            };

            _context.Likes.Add(like);
            liked = true;
        }

        await _notificationService.CreateNotificationAsync(
            userId: post.UserId,
            fromUserId: userId,
            type: NotificationType.Like,
            message: "лайкнул ваш пост",
            postId: post.Id
            );
        await _context.SaveChangesAsync();


        var likesCount = await _context.Likes.CountAsync(l => l.PostId == id);

        return Ok(new
        {
            liked,
            likesCount
        });
    }

    [HttpGet("{id}/comments")]
    [Authorize]
    public async Task<IActionResult> GetComments(int id)
    {
        var currentUserId = User.GetUserId();

        var postExists = await _context.Posts.AnyAsync(p => p.Id == id);

        if (!postExists)
            return NotFound("Post not found");

        var comments = await _context.Comments
            .Where(c => c.PostId == id)
            .Include(c => c.User)
            .Include(c => c.Likes)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();

        var result = comments
            .Where(c => c.ParentCommentId == null)
            .Select(c => new
            {
                c.Id,
                c.Text,
                c.CreatedAt,
                LikesCount = c.Likes.Count,
                IsLikedByCurrentUser = c.Likes.Any(l => l.UserId == currentUserId),
                RepliesCount = comments.Count(r => r.ParentCommentId == c.Id),
                Author = new
                {
                    c.User.Id,
                    c.User.Username,
                    AvatarUrl = c.User.AvatarUrl ?? ""
                },
                Replies = comments
                    .Where(r => r.ParentCommentId == c.Id)
                    .OrderBy(r => r.CreatedAt)
                    .Select(r => new
                    {
                        r.Id,
                        r.Text,
                        r.CreatedAt,
                        LikesCount = r.Likes.Count,
                        IsLikedByCurrentUser = r.Likes.Any(l => l.UserId == currentUserId),
                        Author = new
                        {
                            r.User.Id,
                            r.User.Username,
                            AvatarUrl = r.User.AvatarUrl ?? ""
                        }
                    })
                    .ToList()
            })
            .ToList();

        return Ok(result);
    }

    [HttpPost("{id}/comments")]
    [Authorize]
    public async Task<IActionResult> CreateComment(int id, [FromBody] CreateCommentDto dto)
    {
        var userId = User.GetUserId();

        var post = await _context.Posts
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (post == null)
            return NotFound("Post not found");

        if (string.IsNullOrWhiteSpace(dto.Text))
            return BadRequest("Comment is empty");

        if (dto.Text.Length > 500)
            return BadRequest("Comment is too long");

        Comment? parentComment = null;

        if (dto.ParentCommentId.HasValue)
        {
            parentComment = await _context.Comments
                .Include(c => c.User)
                .FirstOrDefaultAsync(c =>
                    c.Id == dto.ParentCommentId.Value &&
                    c.PostId == id);

            if (parentComment == null)
                return BadRequest("Parent comment not found");

            if (parentComment.ParentCommentId.HasValue)
            {
                dto.ParentCommentId = parentComment.ParentCommentId;
            }
        }

        var comment = new Comment
        {
            Text = dto.Text.Trim(),
            UserId = userId,
            PostId = id,
            ParentCommentId = dto.ParentCommentId
        };

        _context.Comments.Add(comment);
        await _context.SaveChangesAsync();

        var user = await _context.Users.FindAsync(userId);

        if (post.UserId != userId && dto.ParentCommentId == null)
        {
            await _notificationService.CreateNotificationAsync(
                userId: post.UserId,
                fromUserId: userId,
                type: NotificationType.Comment,
                message: $"{user.Username} прокомментировал ваш пост",
                postId: post.Id
            );
        }

        if (parentComment != null && parentComment.UserId != userId)
        {
            var parentCommentAuthor = await _context.Users
                .FirstOrDefaultAsync(u => u.Id == parentComment.UserId);

            if (parentCommentAuthor != null && parentCommentAuthor.NotifyCommentReplies)
            {
                await _notificationService.CreateNotificationAsync(
                    userId: parentComment.UserId,
                    fromUserId: userId,
                    type: NotificationType.CommentReply,
                    message: $"{user.Username} ответил на ваш комментарий",
                    postId: post.Id
                );
            }
        }

        var mentionedUsers = await GetMentionedUsersAsync(comment.Text);

        foreach (var mentionedUser in mentionedUsers)
        {
            if (mentionedUser.Id == userId)
                continue;

            await _notificationService.CreateNotificationAsync(
                userId: mentionedUser.Id,
                fromUserId: userId,
                type: NotificationType.Mention,
                message: "упомянул вас в комментарии",
                postId: post.Id
            );
        }

        return Ok(new
        {
            comment.Id,
            comment.Text,
            comment.CreatedAt,
            comment.ParentCommentId,
            LikesCount = 0,
            IsLikedByCurrentUser = false,
            Author = new
            {
                user!.Id,
                user.Username,
                AvatarUrl = user.AvatarUrl ?? ""
            }
        });
    }

    [HttpDelete("comments/{commentId}")]
    [Authorize]
    public async Task<IActionResult> DeleteComment(int commentId)
    {
        var userId = User.GetUserId();

        var comment = await _context.Comments
            .Include(c => c.Post)
            .Include(c => c.Replies)
            .FirstOrDefaultAsync(c => c.Id == commentId);

        if (comment == null)
            return NotFound();

        var isCommentAuthor = comment.UserId == userId;
        var isPostAuthor = comment.Post.UserId == userId;

        if (!isCommentAuthor && !isPostAuthor)
            return Forbid();

        var replies = await _context.Comments
            .Where(c => c.ParentCommentId == comment.Id)
            .ToListAsync();

        _context.Comments.RemoveRange(replies);
        _context.Comments.Remove(comment);

        await _context.SaveChangesAsync();

        return Ok(new { message = "Comment deleted" });
    }

    [HttpPost("comments/{commentId}/like")]
    [Authorize]
    public async Task<IActionResult> ToggleCommentLike(int commentId)
    {
        var userId = User.GetUserId();
        var user = await _context.Users.FindAsync(userId);


        var comment = await _context.Comments
            .Include(c => c.Likes)
            .FirstOrDefaultAsync(c => c.Id == commentId);

        if (comment == null)
            return NotFound("Comment not found");

        var existingLike = await _context.CommentLikes
            .FirstOrDefaultAsync(l =>
                l.CommentId == commentId &&
                l.UserId == userId);

        var isLiked = false;

        if (existingLike == null)
        {
            var like = new CommentLike
            {
                CommentId = commentId,
                UserId = userId
            };

            _context.CommentLikes.Add(like);
            isLiked = true;

            if (comment.UserId != userId)
            {
                var commentAuthor = await _context.Users
                    .FirstOrDefaultAsync(u => u.Id == comment.UserId);

                if (commentAuthor != null && commentAuthor.NotifyCommentLikes)
                {
                    await _notificationService.CreateNotificationAsync(
                        userId: comment.UserId,
                        fromUserId: userId,
                        type: NotificationType.CommentLike,
                        message: $"{user.Username} лайкнул ваш комментарий",
                        postId: comment.PostId
                    );
                }
            }
        }
        else
        {
            _context.CommentLikes.Remove(existingLike);
            isLiked = false;
        }

        await _context.SaveChangesAsync();

        var likesCount = await _context.CommentLikes
            .CountAsync(l => l.CommentId == commentId);

        return Ok(new
        {
            isLiked,
            likesCount
        });
    }

    private async Task<List<User>> GetMentionedUsersAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<User>();

        var matches = System.Text.RegularExpressions.Regex.Matches(
            text,
            @"@([a-zA-Z0-9_а-яА-ЯёЁ]+)"
        );

        var usernames = matches
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        if (!usernames.Any())
            return new List<User>();

        var users = await _context.Users
            .Where(u => usernames.Contains(u.Username))
            .ToListAsync();

        return users;
    }

    [HttpPost("{id}/favorite")]
    [Authorize]
    public async Task<IActionResult> ToggleFavorite(int id)
    {
        var userId = User.GetUserId();

        var postExists = await _context.Posts.AnyAsync(p => p.Id == id);

        if (!postExists)
            return NotFound("Post not found");

        var existingFavorite = await _context.Favorites
            .FirstOrDefaultAsync(f => f.PostId == id && f.UserId == userId);

        bool isFavorited;

        if (existingFavorite != null)
        {
            _context.Favorites.Remove(existingFavorite);
            isFavorited = false;
        }
        else
        {
            var favorite = new Favorite
            {
                PostId = id,
                UserId = userId
            };

            _context.Favorites.Add(favorite);
            isFavorited = true;
        }

        await _context.SaveChangesAsync();

        return Ok(new
        {
            isFavorited
        });
    }

    [HttpGet("favorites/my")]
    [Authorize]
    public async Task<IActionResult> GetMyFavoritePosts()
    {
        var userId = User.GetUserId();

        var posts = await _context.Favorites
            .Where(f => f.UserId == userId)
            .Include(f => f.Post)
                .ThenInclude(p => p.User)
            .Include(f => f.Post)
                .ThenInclude(p => p.Likes)
            .OrderByDescending(f => f.Id)
            .Select(f => new
            {
                f.Post.Id,
                f.Post.ImageUrl,
                f.Post.Description,
                f.Post.CreatedAt,

                LikesCount = f.Post.Likes.Count,
                IsLiked = f.Post.Likes.Any(l => l.UserId == userId),
                IsFavorited = true,

                Author = new
                {
                    f.Post.User.Id,
                    f.Post.User.Username,
                    AvatarUrl = f.Post.User.AvatarUrl ?? ""
                }
            })
            .ToListAsync();

        return Ok(posts);
    }

    [HttpGet("feed")]
    [Authorize]
    public async Task<IActionResult> GetFeed()
    {
        var currentUserId = User.GetUserId();

        var usersWhoBlockedMe = await _context.BlockedUsers
    .Where(b => b.BlockedId == currentUserId)
    .Select(b => b.BlockerId)
    .ToListAsync();

        var weekAgo = DateTime.UtcNow.AddDays(-7);

        var followingIds = await _context.Follows
            .Where(f => f.FollowerId == currentUserId)
            .Select(f => f.FollowingId)
            .ToListAsync();

        var feedPostsQuery = _context.Posts
            .Include(p => p.User)
            .Include(p => p.Likes)
            .Include(p => p.Favorites)
            .Where(p =>
                !p.IsHidden &&
                !usersWhoBlockedMe.Contains(p.UserId) &&
                p.CreatedAt >= weekAgo &&
                (
                    followingIds.Contains(p.UserId) ||
                    p.UserId == currentUserId
                )
            );

        var feedPostIds = await feedPostsQuery
            .Select(p => p.Id)
            .ToListAsync();

        var randomPosts = await _context.Posts
            .Include(p => p.User)
            .Include(p => p.Likes)
            .Include(p => p.Favorites)
.Where(p =>
    !p.IsHidden &&
    p.UserId != currentUserId &&
    !feedPostIds.Contains(p.Id) &&
    !usersWhoBlockedMe.Contains(p.UserId))
            .OrderBy(p => EF.Functions.Random())
            .Take(5)
            .ToListAsync();

        var feedPosts = await feedPostsQuery
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

        var result = feedPosts
            .Concat(randomPosts)
            .Select(p => new
            {
                p.Id,
                p.Description,
                p.ImageUrl,
                p.CreatedAt,

                User = new
                {
                    p.User.Id,
                    p.User.Username,
                    p.User.AvatarUrl
                },

                LikesCount = p.Likes.Count,
                IsLiked = p.Likes.Any(l => l.UserId == currentUserId),

                FavoritesCount = p.Favorites.Count,
                IsFavorite = p.Favorites.Any(f => f.UserId == currentUserId)
            });

        return Ok(result);
    }

    [HttpGet("interesting")]
    [Authorize]
    public async Task<IActionResult> GetInterestingPosts()
    {
        var currentUserId = User.GetUserId();

        var usersWhoBlockedMe = await _context.BlockedUsers
    .Where(b => b.BlockedId == currentUserId)
    .Select(b => b.BlockerId)
    .ToListAsync();

        var posts = await _context.Posts
            .Include(p => p.User)
            .Include(p => p.Likes)
            .Include(p => p.Favorites)
            .Where(p =>
    !p.IsHidden &&
    p.UserId != currentUserId &&
    !usersWhoBlockedMe.Contains(p.UserId))
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new
            {
                p.Id,
                p.Description,
                p.ImageUrl,
                p.CreatedAt,

                User = new
                {
                    p.User.Id,
                    p.User.Username,
                    p.User.AvatarUrl
                },

                LikesCount = p.Likes.Count,
                IsLiked = p.Likes.Any(l => l.UserId == currentUserId),

                FavoritesCount = p.Favorites.Count,
                IsFavorite = p.Favorites.Any(f => f.UserId == currentUserId)
            })
            .ToListAsync();

        return Ok(posts);
    }
}