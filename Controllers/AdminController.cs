using System.Threading.Tasks;
using DatingApp.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using DatingApp.Api.Dtos;
using Microsoft.AspNetCore.Identity;
using DatingApp.Api.Models;
using Microsoft.Extensions.Options;
using DatingApp.Api.Helpers;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using System.Net;

namespace DatingApp.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AdminController : ControllerBase
    {
        private static IAmazonS3 client;
        private readonly DataContext _context;
        private readonly UserManager<User> _userManager;
        private readonly IOptions<AwsSettings> _awsConfig;
        public AdminController(DataContext context, UserManager<User> userManager, IOptions<AwsSettings> awsConfig)
        {
            this._awsConfig = awsConfig;
            _userManager = userManager;
            _context = context;

            client = new AmazonS3Client(RegionEndpoint.USWest2);
        }


        [Authorize(Policy = "RequireAdminRole")]
        [HttpGet("usersWithRoles")]
        public async Task<IActionResult> GetUsersWithRoles()
        {
            var userList = await (from user in _context.Users
                                  orderby user.UserName
                                  select new
                                  {
                                      Id = user.Id,
                                      UserName = user.UserName,
                                      Roles = (from userRole in user.UserRoles
                                               join role in _context.Roles
                                               on userRole.RoleId
                                               equals role.Id
                                               select role.Name).ToList()
                                  }).ToListAsync();
            return Ok(userList);
        }

        [Authorize(Policy = "RequireAdminRole")]
        [HttpPost("editRoles/{userName}")]
        public async Task<IActionResult> EditRoles(string userName, RoleEditDto roleEditDto)
        {
            var user = await _userManager.FindByNameAsync(userName);

            var userRoles = await _userManager.GetRolesAsync(user);

            var selectedRoles = roleEditDto.RoleNames;

            selectedRoles = selectedRoles ?? new string[] { };
            var result = await _userManager.AddToRolesAsync(user, selectedRoles.Except(userRoles));

            if (!result.Succeeded)
                return BadRequest("Failed to add to roles");

            result = await _userManager.RemoveFromRolesAsync(user, userRoles.Except(selectedRoles));

            if (!result.Succeeded)
                return BadRequest("Failed to remove the roles");

            return Ok(await _userManager.GetRolesAsync(user));
        }

        [Authorize(Policy = "ModeratePhotoRole")]
        [HttpGet("photosForModeration")]
        public async Task<IActionResult> GetPhotosForModeration()
        {
            var photos = await _context.Photos
                .Include(u => u.User)
                .IgnoreQueryFilters()
                .Where(p => p.IsApproved == false)
                .Select(u => new
                {
                    Id = u.Id,
                    UserName = u.User.UserName,
                    Url = u.Url,
                    IsApproved = u.IsApproved
                }).ToListAsync();

            return Ok(photos);
        }

        [Authorize(Policy = "ModeratePhotoRole")]
        [HttpPost("rejectPhoto/{photoId}")]
        public async Task<IActionResult> RejectPhoto(int photoId)
        {
            var photo = await _context.Photos
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.Id == photoId);

            if (photo.IsMain)
                return BadRequest("You cannot reject the main photo");

            if (photo.PublicId != null)
            {

                string fileNameS3 = _context.Photos.FirstOrDefault(x => x.Id == photoId).Url.ToString().Split(@"/").Last();
                string key = _context.Photos.FirstOrDefault(x => x.Id == photoId).UserId + @"/" + fileNameS3;
                ///Remove image from S3
                DeleteObjectsRequest deleteObjectRequest = new DeleteObjectsRequest
                {
                    BucketName = _awsConfig.Value.BucketName
                    //Key = userId + @"/" + newFileNameS3, // This includes the object keys and null version IDs.
                };
                // You can add specific object key to the delete request using the .AddKey.
                deleteObjectRequest.AddKey(key, null);

                try
                {
                    DeleteObjectsResponse response = await client.DeleteObjectsAsync(deleteObjectRequest);
                    // Console.WriteLine("Successfully deleted all the {0} items", response.DeletedObjects.Count);
                    ///DELETE FROM DB
                    if (response.HttpStatusCode == HttpStatusCode.OK)
                    {
                        _context.Photos.Remove(_context.Photos.FirstOrDefault(x => x.Id == photoId));
                    }
                    else
                    {
                        return BadRequest("Error on deleting photo.");
                    }
                }
                catch (DeleteObjectsException e)
                {
                    return BadRequest("Error on deleting photo. " + e.Message);
                }
            }

            if (photo.PublicId == null)
            {
                _context.Photos.Remove(photo);
            }

            await _context.SaveChangesAsync();

            return Ok();
        }

        [Authorize(Policy = "ModeratePhotoRole")]
        [HttpPost("approvePhoto/{photoId}")]
        public async Task<IActionResult> ApprovePhoto(int photoId)
        {
            var photo = await _context.Photos
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.Id == photoId);

            photo.IsApproved = true;

            await _context.SaveChangesAsync();

            return Ok();
        }
    }
}