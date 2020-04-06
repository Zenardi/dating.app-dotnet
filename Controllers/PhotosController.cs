using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using AutoMapper;
using DatingApp.Api.Data;
using DatingApp.Api.Dtos;
using DatingApp.Api.Helpers;
using DatingApp.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DatingApp.Api.Controllers
{
    [Authorize]
    [Route("api/users/{userId}/photos")]
    public class PhotosController : ControllerBase
    {
        private static IAmazonS3 client;
        private readonly IDatingRepository _repo;
        private readonly IMapper _mapper;
        private readonly IOptions<AwsSettings> _awsConfig;
        
        public PhotosController(IDatingRepository repo, IMapper mapper, IOptions<AwsSettings> awsConfig)
        {
            this._awsConfig = awsConfig;
            
            this._mapper = mapper;
            this._repo = repo;

            client = new AmazonS3Client(RegionEndpoint.USWest2);
        }

        [HttpGet("{id}", Name="GetPhoto")]
        public async Task<IActionResult> GetPhoto(int id)
        {
            var photoFromRepo = await _repo.GetPhoto(id);

            var photo = _mapper.Map<PhotoForReturnDto>(photoFromRepo);

            return Ok(photo);
        }


        [HttpPost]
        public async Task<IActionResult> AddPhotoForUser(int userId, [FromForm] PhotoForCreationDto photoForCreationDto)
        {
            if(userId != int.Parse(User.FindFirst(ClaimTypes.NameIdentifier).Value))
            {
                return Unauthorized();
            }
            var userFromRepo = await _repo.GetUser(userId);
            
            var file = photoForCreationDto.File;

            //Result from s3 upload 
            var uploadResult = new PutObjectResponse();

            if(file.Length > 0)
            {
                string fileExtension = Path.GetExtension(file.FileName);
                string newFileNameS3 = file.FileName + "_" + DateTime.Now.ToString("yyyy.MM.dd-hh.mm.ss") + fileExtension;
                photoForCreationDto.DateAdded = DateTime.Now;

                using(var stream = file.OpenReadStream())
                {
                    PutObjectRequest request = new PutObjectRequest()
                    {
                        InputStream = stream,
                        Key = userId + @"/" + newFileNameS3,
                        BucketName = _awsConfig.Value.BucketName
                        
                    };
                    uploadResult = await client.PutObjectAsync(request);
                }
                photoForCreationDto.Url = string.Format("https://s3-{0}.amazonaws.com/{1}/{2}/{3}", _awsConfig.Value.Region, _awsConfig.Value.BucketName, userId, newFileNameS3);
                photoForCreationDto.PublicId = uploadResult.VersionId;


                var photo = _mapper.Map<Photo>(photoForCreationDto);

                if(!userFromRepo.Photos.Any(u=> u.IsMain))
                {
                    photo.IsMain = true;
                }
                userFromRepo.Photos.Add(photo);

                if(await _repo.SaveAll())
                {
                    var photoToReturn = _mapper.Map<PhotoForReturnDto>(photo);
                    return CreatedAtRoute("GetPhoto", new { userId = userId, id = photo.Id }, photoToReturn);
                }
                return BadRequest("Could not add the photo");
            }

            return BadRequest("Could not add the photo because file size is zero");
        }

        [HttpPost("{id}/setMain")]
        public async Task<IActionResult> SetMainPhoto(int userId, int id)
        {
            if(userId != int.Parse(User.FindFirst(ClaimTypes.NameIdentifier).Value)) return Unauthorized();

            var user = await _repo.GetUser(userId);
            if(!user.Photos.Any(p => p.Id == id)) return Unauthorized();

            var photoFromRepo = await _repo.GetPhoto(id);

            if(photoFromRepo.IsMain) return BadRequest("This is already the main photo.");

            var currentMainPhoto = await _repo.GetMainPhotoForUser(userId);
            currentMainPhoto.IsMain = false;
            photoFromRepo.IsMain = true;

            if(await _repo.SaveAll())
                return NoContent();
            return BadRequest("Could not set photo to main");
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeletePhoto(int userId, int id)
        {
            if(userId != int.Parse(User.FindFirst(ClaimTypes.NameIdentifier).Value)) return Unauthorized();

            var user = await _repo.GetUser(userId);
            if(!user.Photos.Any(p => p.Id == id)) return Unauthorized();

            var photoFromRepo = await _repo.GetPhoto(id);

            if(photoFromRepo.IsMain) return BadRequest("You cannot delete your main photo.");

            if(photoFromRepo.PublicId != null)
            {
                string fileNameS3 = photoFromRepo.Url.Split(@"/").Last();
                string key = userId + @"/" + fileNameS3;
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
                    if(response.HttpStatusCode == HttpStatusCode.OK)
                    {
                        _repo.Delete(photoFromRepo);
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

            if(photoFromRepo.PublicId == null)
            {
                _repo.Delete(photoFromRepo);
            }

            if (await _repo.SaveAll()) return Ok();    
           

            return BadRequest("Error on deleting photo");

        }
    }
}